using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Data.Migrations;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KanbanBoard.Tests;

public sealed class PersonalAccessTokenScopeApiTests
{
    private static readonly string InternalSecret = new('s', 32);

    [Fact]
    public async Task CreateAsync_stores_requested_scope_and_rejects_unknown_scope()
    {
        await using var dbContext = TestDb.CreateContext();
        var user = new AppUser { Issuer = "issuer", Subject = "subject" };
        dbContext.Add(user);
        await dbContext.SaveChangesAsync();
        var service = new PersonalAccessTokenService(dbContext, TestDb.PatOptions());

        var readToken = await service.CreateAsync(user, "Reader", null, PersonalAccessTokenScopes.Read, CancellationToken.None);
        var fullToken = await service.CreateAsync(user, "Writer", null, PersonalAccessTokenScopes.Full, CancellationToken.None);

        Assert.Equal(PersonalAccessTokenScopes.Read, readToken.Token.Scope);
        Assert.Equal(PersonalAccessTokenScopes.Read, await service.GetScopeAsync(readToken.Token.Id, CancellationToken.None));
        Assert.Equal(PersonalAccessTokenScopes.Full, await service.GetScopeAsync(fullToken.Token.Id, CancellationToken.None));
        Assert.Null(await service.GetScopeAsync(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(user, "Bad", null, "admin", CancellationToken.None));
    }

    [Fact]
    public async Task GetScopeAsync_has_no_scope_for_a_token_that_is_revoked_or_expired()
    {
        await using var dbContext = TestDb.CreateContext();
        var user = new AppUser { Issuer = "issuer", Subject = "subject" };
        dbContext.Add(user);
        await dbContext.SaveChangesAsync();
        var service = new PersonalAccessTokenService(dbContext, TestDb.PatOptions());
        var revoked = await service.CreateAsync(user, "Revoked", null, PersonalAccessTokenScopes.Full, CancellationToken.None);
        var expired = await service.CreateAsync(user, "Expired", DateTimeOffset.UtcNow.AddSeconds(-1), PersonalAccessTokenScopes.Full, CancellationToken.None);
        var live = await service.CreateAsync(user, "Live", DateTimeOffset.UtcNow.AddHours(1), PersonalAccessTokenScopes.Read, CancellationToken.None);
        Assert.Equal(PersonalAccessTokenScopes.Full, await service.GetScopeAsync(revoked.Token.Id, CancellationToken.None));

        Assert.True(await service.RevokeAsync(user.Id, revoked.Token.Id, CancellationToken.None));

        Assert.Null(await service.GetScopeAsync(revoked.Token.Id, CancellationToken.None));
        Assert.Null(await service.GetScopeAsync(expired.Token.Id, CancellationToken.None));
        // A token that expires later still has its scope.
        Assert.Equal(PersonalAccessTokenScopes.Read, await service.GetScopeAsync(live.Token.Id, CancellationToken.None));
    }

    // The MCP server forwards a token it validated up to its cache window ago. Through the real application.
    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task A_forwarded_token_that_was_revoked_or_expired_since_neither_writes_nor_reads(string state)
    {
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await ProgramDependencyWiringTests.PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "REVOKE" });
        var item = await ProgramDependencyWiringTests.PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Item", type = "Task", status = "Backlog", priority = "Medium" });
        using (var dbContext = api.OpenDatabase())
        {
            var token = await dbContext.PersonalAccessTokens.SingleAsync();
            token.RevokedAtUtc = state == "revoked" ? DateTimeOffset.UtcNow : null;
            token.ExpiresAtUtc = state == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : null;
            await dbContext.SaveChangesAsync();
        }

        HttpResponseMessage[] responses =
        [
            await client.PatchAsJsonAsync($"/api/items/{item.Id}", new { title = "Renamed" }),
            await client.PostAsJsonAsync($"/api/items/{item.Id}/links", new AddWorkItemLinkRequest("pr", "https://github.com/acme/app/pull/42")),
            await client.PostAsJsonAsync($"/api/items/{item.Id}/comments", new { body = "note" }),
            await client.DeleteAsync($"/api/items/{item.Id}"),
            // The API is the one that decides, so the token does not read either.
            await client.GetAsync($"/api/items/{item.Id}"),
            await client.GetAsync("/api/items/changes?fromStart=true")
        ];

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
            Assert.Equal(KanbanErrorCodes.ScopeDenied, error!.Code);
            Assert.Equal(KanbanInternalApiExtensions.UnknownTokenMessage, error.Message);
            response.Dispose();
        }

        Assert.Contains("revoked or has expired", KanbanInternalApiExtensions.UnknownTokenMessage, StringComparison.Ordinal);
        using var stored = api.OpenDatabase();
        Assert.Equal("Item", (await stored.WorkItems.SingleAsync()).Title);
        Assert.Empty(await stored.WorkItemLinks.ToListAsync());
        Assert.Empty(await stored.WorkItemComments.ToListAsync());
        Assert.Equal(WorkItemChangeKinds.ItemCreated, (await stored.WorkItemChanges.SingleAsync()).Kind);
    }

    [Fact]
    public void Model_defaults_token_scope_to_full()
    {
        Assert.Equal(PersonalAccessTokenScopes.Full, new PersonalAccessToken().Scope);

        using var dbContext = TestDb.CreateContext();
        var scopeProperty = dbContext.Model.FindEntityType(typeof(PersonalAccessToken))!.FindProperty(nameof(PersonalAccessToken.Scope))!;
        Assert.False(scopeProperty.IsNullable);
        Assert.Equal(PersonalAccessTokenScopes.Full, scopeProperty.GetDefaultValue());
    }

    [Fact]
    public void Migration_backfills_existing_tokens_as_full()
    {
        var migration = new AddPersonalAccessTokenScope();
        var operation = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));

        Assert.Equal("PersonalAccessTokens", operation.Table);
        Assert.Equal("Scope", operation.Name);
        Assert.False(operation.IsNullable);
        Assert.Equal(PersonalAccessTokenScopes.Full, operation.DefaultValue);

        // The SQL the operator applies must add the column with DEFAULT 'full' so every existing row becomes full.
        using var mySqlContext = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseMySQL("Server=127.0.0.1;Port=3306;Database=kanban_test;User ID=test;Password=test;")
            .Options);
        var sql = string.Join('\n', mySqlContext.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.UpOperations, mySqlContext.Model)
            .Select(command => command.CommandText));
        Assert.Contains("ADD `Scope` varchar(16) NOT NULL DEFAULT 'full'", sql, StringComparison.Ordinal);

        var deployScript = File.ReadAllText(FindRepoFile("deploy/sql/002_personal_access_token_scope.sql"));
        Assert.Contains("ADD COLUMN IF NOT EXISTS `Scope` varchar(16) NOT NULL DEFAULT 'full'", deployScript, StringComparison.Ordinal);
        // Re-running the script must be harmless: the history row is only inserted when missing.
        Assert.Contains("INSERT IGNORE INTO `__EFMigrationsHistory`", deployScript, StringComparison.Ordinal);
        Assert.Contains(migration.GetType().GetCustomAttributes(typeof(MigrationAttribute), false)
            .Cast<MigrationAttribute>().Single().Id, deployScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PersonalAccessTokenScopes.Read)]
    [InlineData(PersonalAccessTokenScopes.Full)]
    public async Task Validation_endpoint_returns_token_scope(string scope)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var created = await fixture.CreateTokenAsync(scope);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/internal/personal-access-tokens/validate")
        {
            Content = JsonContent.Create(new ValidatePersonalAccessTokenRequest(created.PlaintextToken))
        };
        request.Headers.TryAddWithoutValidation(KanbanInternalAuth.SecretHeaderName, InternalSecret);

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ValidatedPersonalAccessTokenDto>();
        Assert.NotNull(dto);
        Assert.Equal(created.Token.Id, dto.PersonalAccessTokenId);
        Assert.Equal(scope, dto.Scope);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Internal_write_requests_for_read_token_are_refused(string method)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var created = await fixture.CreateTokenAsync(PersonalAccessTokenScopes.Read);

        var response = await fixture.Client.SendAsync(fixture.InternalRequest(new HttpMethod(method), created.Token.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("'read' scope", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.WriteHandlerCalls);
    }

    [Theory]
    [InlineData("random-guid")]
    [InlineData("not-a-guid")]
    public async Task Internal_write_requests_for_unknown_token_id_are_refused(string tokenIdKind)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var request = fixture.InternalRequest(HttpMethod.Post, tokenId: null);
        request.Headers.TryAddWithoutValidation(
            KanbanInternalAuth.PersonalAccessTokenIdHeaderName,
            tokenIdKind == "random-guid" ? Guid.NewGuid().ToString() : tokenIdKind);

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("does not exist", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.WriteHandlerCalls);
    }

    [Fact]
    public async Task Internal_reads_for_read_token_and_writes_for_full_or_unscoped_requests_pass()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var readToken = await fixture.CreateTokenAsync(PersonalAccessTokenScopes.Read);
        var fullToken = await fixture.CreateTokenAsync(PersonalAccessTokenScopes.Full);

        var readResponse = await fixture.Client.SendAsync(fixture.InternalRequest(HttpMethod.Get, readToken.Token.Id));
        var fullWrite = await fixture.Client.SendAsync(fixture.InternalRequest(HttpMethod.Post, fullToken.Token.Id));
        var noTokenWrite = await fixture.Client.SendAsync(fixture.InternalRequest(HttpMethod.Post, tokenId: null));

        Assert.Equal(HttpStatusCode.NoContent, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, fullWrite.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, noTokenWrite.StatusCode);
        Assert.Equal(2, fixture.WriteHandlerCalls);
    }

    [Fact]
    public async Task Settings_create_endpoint_accepts_scope_defaults_to_full_and_rejects_unknown()
    {
        using var fixture = await ApiFixture.CreateAsync();

        var readResponse = await fixture.Client.PostAsJsonAsync("/api/me/personal-access-tokens",
            new CreatePersonalAccessTokenRequest("Reader", null, "read"));
        var defaultResponse = await fixture.Client.PostAsJsonAsync("/api/me/personal-access-tokens",
            new { name = "Legacy client", expiresAtUtc = (DateTimeOffset?)null });
        var badResponse = await fixture.Client.PostAsJsonAsync("/api/me/personal-access-tokens",
            new CreatePersonalAccessTokenRequest("Bad", null, "admin"));

        Assert.Equal(HttpStatusCode.Created, readResponse.StatusCode);
        Assert.Equal("read", (await readResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>())!.Token.Scope);
        Assert.Equal(HttpStatusCode.Created, defaultResponse.StatusCode);
        Assert.Equal("full", (await defaultResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>())!.Token.Scope);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        var listed = await fixture.Client.GetFromJsonAsync<List<PersonalAccessTokenMetadataDto>>("/api/me/personal-access-tokens");
        Assert.NotNull(listed);
        Assert.Equal(["full", "read"], listed.Select(token => token.Scope).Order().ToArray());
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {relativePath} above {AppContext.BaseDirectory}.");
    }

    private sealed class ApiFixture : IDisposable
    {
        private readonly IHost host;
        private readonly WriteCounter writeCounter;

        private ApiFixture(IHost host, WriteCounter writeCounter)
        {
            this.host = host;
            this.writeCounter = writeCounter;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }

        public int WriteHandlerCalls => writeCounter.Count;

        public static async Task<ApiFixture> CreateAsync()
        {
            var databaseName = Guid.NewGuid().ToString();
            var writeCounter = new WriteCounter();
            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddDbContext<KanbanDbContext>(options => options.UseInMemoryDatabase(databaseName));
                        services.AddSingleton(TestDb.PatOptions());
                        services.AddScoped<IPersonalAccessTokenService, PersonalAccessTokenService>();
                        services.AddScoped<ICurrentUserService, FixedUserService>();
                        services.AddSingleton(writeCounter);
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseKanbanInternalApiAuthentication(new InternalApiOptions { SharedSecret = InternalSecret });
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapKanbanSettingsEndpoints();
                            endpoints.MapKanbanInternalApiEndpoints();
                            endpoints.MapMethods("/api/items", ["GET", "POST", "PUT", "PATCH", "DELETE"], (HttpContext context, WriteCounter counter) =>
                            {
                                if (!HttpMethods.IsGet(context.Request.Method))
                                {
                                    counter.Count++;
                                }

                                return Results.NoContent();
                            });
                        });
                    });
                })
                .StartAsync();

            return new ApiFixture(host, writeCounter);
        }

        public async Task<CreatedPersonalAccessToken> CreateTokenAsync(string scope)
        {
            using var scopeServices = host.Services.CreateScope();
            var dbContext = scopeServices.ServiceProvider.GetRequiredService<KanbanDbContext>();
            var user = await FixedUserService.GetOrAddAsync(dbContext);
            var service = scopeServices.ServiceProvider.GetRequiredService<IPersonalAccessTokenService>();
            return await service.CreateAsync(user, $"token-{scope}", null, scope, CancellationToken.None);
        }

        public HttpRequestMessage InternalRequest(HttpMethod method, Guid? tokenId)
        {
            var request = new HttpRequestMessage(method, "/api/items");
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
        }
    }

    private sealed class WriteCounter
    {
        public int Count { get; set; }
    }

    private sealed class FixedUserService(KanbanDbContext dbContext) : ICurrentUserService
    {
        public Task<AppUser> GetOrCreateCurrentUserAsync(CancellationToken cancellationToken) => GetOrAddAsync(dbContext);

        public bool TryGetExternalIdentity(ClaimsPrincipal principal, out ExternalUserIdentity identity)
        {
            identity = new ExternalUserIdentity("issuer", "subject", null, null);
            return true;
        }

        public static async Task<AppUser> GetOrAddAsync(KanbanDbContext dbContext)
        {
            var user = await dbContext.AppUsers.FirstOrDefaultAsync(candidate => candidate.Subject == "subject");
            if (user is null)
            {
                user = new AppUser { Issuer = "issuer", Subject = "subject" };
                dbContext.AppUsers.Add(user);
                await dbContext.SaveChangesAsync();
            }

            return user;
        }
    }
}
