using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Data.Migrations;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KanbanBoard.Tests;

// The claim endpoints run against SQLite on a file: unlike the in-memory provider it executes the conditional
// UPDATE as real SQL, and every request gets its own connection, so concurrent claims genuinely race.
public sealed class WorkItemClaimApiTests
{
    private static readonly string InternalSecret = new('s', 32);

    [Fact]
    public async Task Factory_identity_created_without_login_claims_as_itself_ignoring_any_supplied_name()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var output = new StringWriter();
        var exitCode = await fixture.WithServicesAsync(services => ServiceIdentityCommand.RunAsync(
            [ServiceIdentityCommand.CommandName, "--name", "Factory"], services, output, TextWriter.Null, CancellationToken.None));
        Assert.Equal(0, exitCode);
        var fullToken = await fixture.ValidateAsync(PrintedToken(output, "full-scope token: "));
        var itemId = await fixture.AddItemAsync();

        // The caller tries to name someone else in the body and in the forwarded user id header.
        var request = fixture.InternalRequest($"/api/items/{itemId}/claim", fullToken.Token.Id, new
        {
            marker = "run-1",
            ttlSeconds = 60,
            claimant = "mallory",
            claimantName = "mallory",
            claimantId = Guid.NewGuid(),
            author = "mallory"
        });
        request.Headers.Remove(KanbanInternalAuth.AppUserIdHeaderName);
        request.Headers.TryAddWithoutValidation(KanbanInternalAuth.AppUserIdHeaderName, Guid.NewGuid().ToString());
        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await fixture.GetItemAsync(itemId);
        Assert.Equal(fullToken.User.Id, stored.ClaimantUserId);
        Assert.Equal("Factory", stored.ClaimantName);
        Assert.Equal(ServiceIdentityProvisioner.Issuer, fullToken.User.Issuer);
    }

    [Fact]
    public async Task Claim_on_unclaimed_item_records_claimant_marker_and_expiry_in_snake_case_fields()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var itemId = await fixture.AddItemAsync();

        var response = await fixture.ClaimAsync(itemId, caller, "run-1", 90);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Factory", json.RootElement.GetProperty("claimant").GetString());
        Assert.Equal(caller.User.Id, json.RootElement.GetProperty("claimant_id").GetGuid());
        Assert.Equal("run-1", json.RootElement.GetProperty("claim_marker").GetString());
        Assert.Equal(fixture.Clock.Now.AddSeconds(90), json.RootElement.GetProperty("claim_expires_at_utc").GetDateTimeOffset());
        Assert.Equal(itemId, json.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Same_caller_and_marker_can_claim_again_and_gets_a_new_expiry()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var itemId = await fixture.AddItemAsync();
        await fixture.ClaimAsync(itemId, caller, "run-1", 60);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));

        var again = await fixture.ClaimAsync(itemId, caller, "run-1", 60);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(fixture.Clock.Now.AddSeconds(60), (await fixture.GetItemAsync(itemId)).ClaimExpiresAtUtc);
    }

    [Theory]
    [InlineData(true, "run-2")]   // same identity, another marker
    [InlineData(false, "run-1")]  // another identity, same marker
    [InlineData(true, "RUN-1")]   // markers are compared exactly
    public async Task Second_claim_on_a_live_claim_is_refused_already_claimed_and_changes_nothing(bool sameIdentity, string secondMarker)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var first = await fixture.CreateCallerAsync("Factory");
        var second = sameIdentity ? first : await fixture.CreateCallerAsync("Other");
        var itemId = await fixture.AddItemAsync();
        await fixture.ClaimAsync(itemId, first, "run-1", 60);
        var before = await fixture.GetItemAsync(itemId);

        var response = await fixture.ClaimAsync(itemId, second, secondMarker, 600);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed);
        var after = await fixture.GetItemAsync(itemId);
        Assert.Equal(first.User.Id, after.ClaimantUserId);
        Assert.Equal("run-1", after.ClaimMarker);
        Assert.Equal(before.ClaimExpiresAtUtc, after.ClaimExpiresAtUtc);
    }

    [Fact]
    public async Task Expired_claim_can_be_taken_by_another_marker_but_not_one_second_early()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var first = await fixture.CreateCallerAsync("Factory");
        var second = await fixture.CreateCallerAsync("Other");
        var itemId = await fixture.AddItemAsync();
        await fixture.ClaimAsync(itemId, first, "run-1", 10);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        var early = await fixture.ClaimAsync(itemId, second, "run-2", 60);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var afterExpiry = await fixture.ClaimAsync(itemId, second, "run-2", 60);

        await AssertErrorAsync(early, HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed);
        Assert.Equal(HttpStatusCode.OK, afterExpiry.StatusCode);
        var stored = await fixture.GetItemAsync(itemId);
        Assert.Equal(second.User.Id, stored.ClaimantUserId);
        Assert.Equal("Other", stored.ClaimantName);
        Assert.Equal("run-2", stored.ClaimMarker);
    }

    [Fact]
    public async Task Concurrent_claims_produce_exactly_one_winner()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        const int contenders = 24;

        // Several rounds: a read-then-write implementation loses this race only some of the time.
        for (var round = 0; round < 5; round++)
        {
            var itemId = await fixture.AddItemAsync();
            using var start = new SemaphoreSlim(0);
            var attempts = Enumerable.Range(0, contenders)
                .Select(index => Task.Run(async () =>
                {
                    await start.WaitAsync();
                    var response = await fixture.ClaimAsync(itemId, caller, $"run-{index}", 60);
                    return (Marker: $"run-{index}", Response: response);
                }))
                .ToList();
            start.Release(contenders);
            var results = await Task.WhenAll(attempts);

            var winner = Assert.Single(results, result => result.Response.StatusCode == HttpStatusCode.OK);
            foreach (var loser in results.Where(result => result != winner))
            {
                await AssertErrorAsync(loser.Response, HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed);
            }

            Assert.Equal(winner.Marker, (await fixture.GetItemAsync(itemId)).ClaimMarker);
            var winnerDto = await winner.Response.Content.ReadFromJsonAsync<WorkItemDto>();
            Assert.Equal(winner.Marker, winnerDto!.ClaimMarker);
        }
    }

    [Fact]
    public async Task Renew_extends_a_live_claim_held_by_the_caller()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var itemId = await fixture.AddItemAsync();
        await fixture.ClaimAsync(itemId, caller, "run-1", 10);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));

        var response = await fixture.PostAsync($"/api/items/{itemId}/claim/renew", caller, new ClaimWorkItemRequest("run-1", 120));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<WorkItemDto>();
        Assert.Equal(fixture.Clock.Now.AddSeconds(120), dto!.ClaimExpiresAtUtc);
        Assert.Equal("run-1", dto.ClaimMarker);
        Assert.Equal(fixture.Clock.Now.AddSeconds(120), (await fixture.GetItemAsync(itemId)).ClaimExpiresAtUtc);
    }

    [Fact]
    public async Task Renew_is_refused_when_the_claim_is_not_the_callers_live_claim()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var other = await fixture.CreateCallerAsync("Other");
        var neverClaimed = await fixture.AddItemAsync();
        var itemId = await fixture.AddItemAsync();
        await fixture.ClaimAsync(itemId, caller, "run-1", 10);
        var before = await fixture.GetItemAsync(itemId);

        var otherMarker = await fixture.PostAsync($"/api/items/{itemId}/claim/renew", caller, new ClaimWorkItemRequest("run-2", 60));
        var otherIdentity = await fixture.PostAsync($"/api/items/{itemId}/claim/renew", other, new ClaimWorkItemRequest("run-1", 60));
        var unclaimed = await fixture.PostAsync($"/api/items/{neverClaimed}/claim/renew", caller, new ClaimWorkItemRequest("run-1", 60));
        Assert.Equal(before.ClaimExpiresAtUtc, (await fixture.GetItemAsync(itemId)).ClaimExpiresAtUtc);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var lapsed = await fixture.PostAsync($"/api/items/{itemId}/claim/renew", caller, new ClaimWorkItemRequest("run-1", 60));

        await AssertErrorAsync(otherMarker, HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed);
        await AssertErrorAsync(otherIdentity, HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed);
        await AssertErrorAsync(unclaimed, HttpStatusCode.Conflict, KanbanErrorCodes.Conflict);
        await AssertErrorAsync(lapsed, HttpStatusCode.Conflict, KanbanErrorCodes.Conflict);
        Assert.Equal(before.ClaimExpiresAtUtc, (await fixture.GetItemAsync(itemId)).ClaimExpiresAtUtc);
    }

    [Fact]
    public async Task Release_clears_the_claim_so_another_marker_can_claim()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var other = await fixture.CreateCallerAsync("Other");
        var itemId = await fixture.AddItemAsync();
        await fixture.ClaimAsync(itemId, caller, "run-1", 600);

        var release = await fixture.PostAsync($"/api/items/{itemId}/claim/release", caller, new ReleaseWorkItemClaimRequest("run-1"));

        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        var dto = await release.Content.ReadFromJsonAsync<WorkItemDto>();
        Assert.Null(dto!.Claimant);
        Assert.Null(dto.ClaimantId);
        Assert.Null(dto.ClaimMarker);
        Assert.Null(dto.ClaimExpiresAtUtc);
        var stored = await fixture.GetItemAsync(itemId);
        Assert.Null(stored.ClaimantUserId);
        Assert.Null(stored.ClaimantName);
        Assert.Null(stored.ClaimMarker);
        Assert.Null(stored.ClaimExpiresAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await fixture.ClaimAsync(itemId, other, "run-2", 60)).StatusCode);
    }

    [Fact]
    public async Task Claim_renew_and_release_each_send_one_board_change_and_a_refused_claim_sends_none()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var other = await fixture.CreateCallerAsync("Other");
        var itemId = await fixture.AddItemAsync();
        using var changes = fixture.SubscribeToBoardChanges();
        int Drain() { var count = 0; while (changes.Reader.TryRead(out _)) count++; return count; }

        Assert.Equal(HttpStatusCode.OK, (await fixture.ClaimAsync(itemId, caller, "run-1", 60)).StatusCode);
        var afterClaim = Drain();
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.ClaimAsync(itemId, other, "run-2", 60)).StatusCode);
        var afterRefused = Drain();
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync($"/api/items/{itemId}/claim/renew", caller, new ClaimWorkItemRequest("run-1", 120))).StatusCode);
        var afterRenew = Drain();
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync($"/api/items/{itemId}/claim/release", caller, new ReleaseWorkItemClaimRequest("run-1"))).StatusCode);
        var afterRelease = Drain();

        Assert.Equal((1, 0, 1, 1), (afterClaim, afterRefused, afterRenew, afterRelease));
    }

    [Fact]
    public async Task Release_is_refused_when_the_caller_does_not_hold_the_claim()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var other = await fixture.CreateCallerAsync("Other");
        var neverClaimed = await fixture.AddItemAsync();
        var itemId = await fixture.AddItemAsync();
        await fixture.ClaimAsync(itemId, caller, "run-1", 600);

        var otherMarker = await fixture.PostAsync($"/api/items/{itemId}/claim/release", caller, new ReleaseWorkItemClaimRequest("run-2"));
        var otherIdentity = await fixture.PostAsync($"/api/items/{itemId}/claim/release", other, new ReleaseWorkItemClaimRequest("run-1"));
        var unclaimed = await fixture.PostAsync($"/api/items/{neverClaimed}/claim/release", caller, new ReleaseWorkItemClaimRequest("run-1"));

        await AssertErrorAsync(otherMarker, HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed);
        await AssertErrorAsync(otherIdentity, HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed);
        await AssertErrorAsync(unclaimed, HttpStatusCode.Conflict, KanbanErrorCodes.Conflict);
        Assert.Equal("run-1", (await fixture.GetItemAsync(itemId)).ClaimMarker);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("claim/renew")]
    [InlineData("claim/release")]
    public async Task Unknown_item_is_refused_not_found(string route)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");

        var response = await fixture.PostAsync($"/api/items/{Guid.NewGuid()}/{route}", caller, new ClaimWorkItemRequest("run-1", 60));

        await AssertErrorAsync(response, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
    }

    [Theory]
    [InlineData("", 60)]
    [InlineData("   ", 60)]
    [InlineData("run-1", 0)]
    [InlineData("run-1", -5)]
    [InlineData("run-1", WorkItemClaimLimits.MaxTtlSeconds + 1)]
    public async Task Invalid_marker_or_ttl_is_refused_invalid_argument(string marker, int ttlSeconds)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var itemId = await fixture.AddItemAsync();

        var response = await fixture.ClaimAsync(itemId, caller, marker, ttlSeconds);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Null((await fixture.GetItemAsync(itemId)).ClaimantUserId);
    }

    [Fact]
    public async Task Overlong_marker_is_refused_invalid_argument()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var itemId = await fixture.AddItemAsync();

        var response = await fixture.ClaimAsync(itemId, caller, new string('m', WorkItemClaimLimits.MaxMarkerLength + 1), 60);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("claim/renew")]
    [InlineData("claim/release")]
    public async Task Callers_without_a_usable_token_identity_cannot_claim(string route)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var readCaller = await fixture.CreateCallerAsync("Reader", PersonalAccessTokenScopes.Read);
        var revokedCaller = await fixture.CreateCallerAsync("Revoked");
        await fixture.WithServicesAsync(services => services.GetRequiredService<IPersonalAccessTokenService>()
            .RevokeAsync(revokedCaller.User.Id, revokedCaller.Token.Id, CancellationToken.None));
        // Expiring exactly now is already expired, as in PersonalAccessTokenService.ValidateAsync.
        var expiredCaller = await fixture.CreateCallerAsync("Expired", expiresAtUtc: fixture.Clock.Now);
        var itemId = await fixture.AddItemAsync();
        var path = $"/api/items/{itemId}/{route}";
        var body = new ClaimWorkItemRequest("run-1", 60);

        var readToken = await fixture.PostAsync(path, readCaller, body);
        var revokedToken = await fixture.PostAsync(path, revokedCaller, body);
        var expiredToken = await fixture.PostAsync(path, expiredCaller, body);
        var unknownToken = await fixture.Client.SendAsync(fixture.InternalRequest(path, Guid.NewGuid(), body));
        var internalWithoutToken = await fixture.Client.SendAsync(fixture.InternalRequest(path, tokenId: null, body));
        var notInternal = await fixture.Client.PostAsJsonAsync(path, body);

        await AssertErrorAsync(readToken, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        await AssertErrorAsync(revokedToken, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        await AssertErrorAsync(expiredToken, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        await AssertErrorAsync(unknownToken, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        await AssertErrorAsync(internalWithoutToken, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        await AssertErrorAsync(notInternal, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        Assert.Null((await fixture.GetItemAsync(itemId)).ClaimantUserId);
    }

    [Fact]
    public void Reads_report_a_live_claim_and_hide_an_expired_one()
    {
        var now = DateTimeOffset.UtcNow;
        var claimantId = Guid.NewGuid();
        var item = new WorkItem
        {
            Title = "Item",
            ClaimantUserId = claimantId,
            ClaimantName = "Factory",
            ClaimMarker = "run-1",
            ClaimExpiresAtUtc = now.AddSeconds(1)
        };

        var live = WorkItemDtoMapper.ToDto(item, now, new AppOptions());
        var expired = WorkItemDtoMapper.ToDto(item, now.AddSeconds(1), new AppOptions());
        var unclaimed = WorkItemDtoMapper.ToDto(new WorkItem { Title = "Free" }, now, new AppOptions());

        Assert.Equal(("Factory", claimantId, "run-1", now.AddSeconds(1)),
            (live.Claimant, live.ClaimantId, live.ClaimMarker, live.ClaimExpiresAtUtc)!);
        Assert.Equal((null, null, null, null),
            (expired.Claimant, expired.ClaimantId, expired.ClaimMarker, expired.ClaimExpiresAtUtc));
        Assert.Equal((null, null, null, null),
            (unclaimed.Claimant, unclaimed.ClaimantId, unclaimed.ClaimMarker, unclaimed.ClaimExpiresAtUtc));
    }

    [Fact]
    public void Migration_and_deploy_script_give_the_marker_column_a_binary_collation()
    {
        // Under MariaDB's default case-insensitive collation "RUN-1" would count as the holder's own marker "run-1".
        var migration = new AddWorkItemClaim();
        var sql = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Contains("MODIFY `ClaimMarker` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL", sql, StringComparison.Ordinal);
        Assert.Equal(
            ["ClaimExpiresAtUtc", "ClaimMarker", "ClaimantName", "ClaimantUserId"],
            migration.UpOperations.OfType<AddColumnOperation>().Select(operation => operation.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(migration.UpOperations.OfType<AddColumnOperation>(), operation => Assert.True(operation.IsNullable));

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy/sql/003_work_item_claim.sql")))
        {
            directory = directory.Parent;
        }

        Assert.Contains(sql, File.ReadAllText(Path.Combine(directory!.FullName, "deploy/sql/003_work_item_claim.sql")), StringComparison.Ordinal);
    }

    private static string PrintedToken(StringWriter output, string label) =>
        output.ToString().Split('\n').Single(line => line.StartsWith(label, StringComparison.Ordinal))[label.Length..].Trim();

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status} but got {response.StatusCode}: {body}");
        var error = JsonSerializer.Deserialize<KanbanErrorDto>(body, JsonSerializerOptions.Web);
        Assert.Equal(code, error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    internal sealed class MutableClock : TimeProvider
    {
        // Whole seconds, so values survive the round trip through any provider's datetime precision.
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    internal sealed class ApiFixture : IDisposable
    {
        private readonly IHost host;
        private readonly string databasePath;

        private ApiFixture(IHost host, string databasePath, MutableClock clock)
        {
            this.host = host;
            this.databasePath = databasePath;
            Clock = clock;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }

        public MutableClock Clock { get; }

        public static async Task<ApiFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"kanban-claims-{Guid.NewGuid():N}.db");
            var clock = new MutableClock();
            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddDbContext<KanbanDbContext>(options => options.UseSqlite($"Data Source={databasePath};Pooling=False"));
                        services.AddSingleton(TestDb.PatOptions());
                        services.AddScoped<IPersonalAccessTokenService, PersonalAccessTokenService>();
                        services.AddSingleton<TimeProvider>(clock);
                        services.AddSingleton<BoardChangeNotifier>();
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseKanbanInternalApiAuthentication(new InternalApiOptions { SharedSecret = InternalSecret });
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapKanbanProjectEndpoints();
                            endpoints.MapKanbanClaimEndpoints();
                            endpoints.MapKanbanDependencyEndpoints();
                            endpoints.MapKanbanLinkEndpoints();
                            endpoints.MapKanbanCommentEndpoints();
                            endpoints.MapKanbanWorkItemReadEndpoints();
                        });
                    });
                })
                .StartAsync();

            var fixture = new ApiFixture(host, databasePath, clock);
            await fixture.WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().Database.EnsureCreatedAsync());
            return fixture;
        }

        public async Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            using var scope = host.Services.CreateScope();
            return await action(scope.ServiceProvider);
        }

        public Task<PersonalAccessTokenValidationResult> CreateCallerAsync(
            string name, string scope = PersonalAccessTokenScopes.Full, DateTimeOffset? expiresAtUtc = null) =>
            WithServicesAsync(async services =>
            {
                var dbContext = services.GetRequiredService<KanbanDbContext>();
                var user = new AppUser { Issuer = "issuer", Subject = name, DisplayName = name };
                dbContext.AppUsers.Add(user);
                await dbContext.SaveChangesAsync();
                var created = await services.GetRequiredService<IPersonalAccessTokenService>()
                    .CreateAsync(user, $"token-{name}", expiresAtUtc, scope, CancellationToken.None);
                return new PersonalAccessTokenValidationResult(user, created.Token);
            });

        public Task<PersonalAccessTokenValidationResult> ValidateAsync(string plaintextToken) =>
            WithServicesAsync(async services =>
                await services.GetRequiredService<IPersonalAccessTokenService>().ValidateAsync(plaintextToken, CancellationToken.None)
                ?? throw new InvalidOperationException("The token did not validate."));

        public BoardChangeSubscription SubscribeToBoardChanges() =>
            host.Services.GetRequiredService<BoardChangeNotifier>().Subscribe();

        public Task<Guid> AddItemAsync(string title = "Item", string projectKey = "PRJ") =>
            WithServicesAsync(async services =>
            {
                var dbContext = services.GetRequiredService<KanbanDbContext>();
                var project = await dbContext.Projects.FirstOrDefaultAsync(candidate => candidate.Key == projectKey);
                if (project is null)
                {
                    project = new Project { Name = "Project", Key = projectKey };
                    dbContext.Projects.Add(project);
                }

                var item = new WorkItem { ProjectId = project.Id, Title = title };
                dbContext.WorkItems.Add(item);
                await dbContext.SaveChangesAsync();
                return item.Id;
            });

        public Task<WorkItem> GetItemAsync(Guid itemId) =>
            WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().WorkItems.AsNoTracking().SingleAsync(item => item.Id == itemId));

        public Task<HttpResponseMessage> ClaimAsync(Guid itemId, PersonalAccessTokenValidationResult caller, string marker, int ttlSeconds) =>
            PostAsync($"/api/items/{itemId}/claim", caller, new ClaimWorkItemRequest(marker, ttlSeconds));

        public Task<HttpResponseMessage> PostAsync(string path, PersonalAccessTokenValidationResult caller, object body) =>
            Client.SendAsync(InternalRequest(path, caller.Token.Id, body));

        // What the MCP server sends: the shared secret plus the ids of the validated token and its user.
        public HttpRequestMessage InternalRequest(string path, Guid? tokenId, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation(KanbanInternalAuth.SecretHeaderName, InternalSecret);
            request.Headers.TryAddWithoutValidation(KanbanInternalAuth.AppUserIdHeaderName, Guid.NewGuid().ToString());
            if (tokenId is not null)
            {
                request.Headers.TryAddWithoutValidation(KanbanInternalAuth.PersonalAccessTokenIdHeaderName, tokenId.ToString());
            }

            return request;
        }

        public void Dispose()
        {
            Client.Dispose();
            host.Dispose();
            File.Delete(databasePath);
        }
    }
}
