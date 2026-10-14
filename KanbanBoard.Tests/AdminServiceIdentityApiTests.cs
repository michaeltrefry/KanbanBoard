using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Tests;

// The admin page's endpoints, through the real application with authentication enabled. A browser session is stood in
// for by a test scheme that signs the caller in as the OIDC subject named in a header.
public sealed class AdminServiceIdentityApiTests
{
    private const string AdminSubject = "admin-sub";
    private const string OidcIssuer = "https://idp.example/realms/test";
    private const string SubjectHeader = "X-Test-Subject";
    private const string NameIdentifierHeader = "X-Test-Subject-As-Name-Identifier";

    [Fact]
    public async Task An_admin_sees_the_service_identities_with_their_active_token_count_and_most_recent_use()
    {
        using var api = new AdminApi();
        var now = DateTimeOffset.UtcNow;
        var factory = Identity("Factory", now.AddDays(-3));
        var idle = Identity("Idle", now.AddDays(-1));
        var person = new AppUser { Issuer = OidcIssuer, Subject = "person", DisplayName = "A person", CreatedAtUtc = now };
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.AddRange(factory, idle, person);
            dbContext.PersonalAccessTokens.AddRange(
                Token(factory, lastUsed: now.AddHours(-5)),
                Token(factory),
                // The most recent use is that of a token revoked since; neither it nor the expired one is active.
                Token(factory, lastUsed: now.AddHours(-1), revoked: now.AddMinutes(-30)),
                Token(factory, lastUsed: now.AddHours(-2), expires: now.AddMinutes(-1)),
                Token(factory, expires: now.AddDays(1)),
                Token(person, lastUsed: now));
        });
        using var client = api.CreateSessionClient(AdminSubject);

        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/me", ProgramDependencyWiringTests.Json);
        var listed = await client.GetFromJsonAsync<List<ServiceIdentitySummaryDto>>("/api/admin/service-identities", ProgramDependencyWiringTests.Json);

        Assert.True(me!.IsAdmin);
        Assert.Equal(
            [
                new ServiceIdentitySummaryDto(factory.Id, "Factory", factory.CreatedAtUtc, 3, now.AddHours(-1)),
                new ServiceIdentitySummaryDto(idle.Id, "Idle", idle.CreatedAtUtc, 0, null)
            ],
            listed!.Select(identity => identity with
            {
                CreatedAtUtc = identity.CreatedAtUtc.ToUniversalTime(),
                LastUsedAtUtc = identity.LastUsedAtUtc?.ToUniversalTime()
            }).ToList());
    }

    [Theory]
    [InlineData("admin-sub other-admin", "someone-else")]
    // The subject of an admin, in another case or as part of a longer value, is not on the list.
    [InlineData("admin-sub", "ADMIN-SUB")]
    [InlineData("admin-sub", "admin")]
    // With no admins configured, nobody is one.
    [InlineData("", AdminSubject)]
    [InlineData(null, AdminSubject)]
    public async Task A_signed_in_user_who_is_not_on_the_list_is_refused_by_every_admin_endpoint(string? adminSubjects, string subject)
    {
        using var api = new AdminApi(adminSubjects);
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var client = api.CreateSessionClient(subject);

        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/me", ProgramDependencyWiringTests.Json);

        Assert.False(me!.IsAdmin);
        await AssertEveryAdminEndpointRefusesAsync(client, identity.Id, withCsrfToken: true);
        await AssertEveryAdminEndpointRefusesAsync(client, identity.Id, withCsrfToken: false);
        await api.AssertNoTokenAsync();
    }

    [Fact]
    public async Task The_mcp_servers_path_never_counts_as_an_admin()
    {
        using var api = new AdminApi($"{AdminSubject} kanban-internal-service");
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));

        // A request of the MCP server, with and without a forwarded token.
        using var mcp = api.CreateCallerClient();
        using var internalOnly = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        internalOnly.DefaultRequestHeaders.Add(KanbanInternalAuth.SecretHeaderName, ProgramDependencyWiringTests.ApiApplication.InternalSecret);
        // An admin's browser session that also carries an Authorization header, which the antiforgery check lets through.
        using var bearer = api.CreateSessionClient(AdminSubject);
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "kbp_anything");

        await AssertEveryAdminEndpointRefusesAsync(mcp, identity.Id, withCsrfToken: false);
        await AssertEveryAdminEndpointRefusesAsync(internalOnly, identity.Id, withCsrfToken: false);
        await AssertEveryAdminEndpointRefusesAsync(bearer, identity.Id, withCsrfToken: true);
        await api.AssertNoTokenAsync(exceptCallerTokens: 1);
    }

    [Fact]
    public async Task With_authentication_disabled_nobody_is_an_admin()
    {
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        // Even with the subject the MCP server's requests are signed in as on the list.
        using var factory = api.WithWebHostBuilder(builder => builder.UseSetting("Auth:AdminSubjects", "kanban-internal-service"));
        using var anonymous = factory.CreateClient();
        using var mcp = factory.CreateClient();
        mcp.DefaultRequestHeaders.Add(KanbanInternalAuth.SecretHeaderName, ProgramDependencyWiringTests.ApiApplication.InternalSecret);

        await AssertEveryAdminEndpointRefusesAsync(anonymous, Guid.NewGuid(), withCsrfToken: false);
        await AssertEveryAdminEndpointRefusesAsync(mcp, Guid.NewGuid(), withCsrfToken: false);
    }

    [Fact]
    public async Task An_admin_issues_a_token_for_a_service_identity_that_works_through_the_mcp_path_with_its_scope()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var admin = api.CreateSessionClient(AdminSubject);
        var expires = DateTimeOffset.UtcNow.AddDays(30);

        using var readResponse = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/tokens",
            new CreatePersonalAccessTokenRequest(" Reader ", expires, "read"), withCsrfToken: true);
        using var fullResponse = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/tokens",
            new { name = "Writer" }, withCsrfToken: true);

        Assert.Equal(HttpStatusCode.Created, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fullResponse.StatusCode);
        // The plaintext is a one-time secret: no cache may keep it.
        Assert.True(readResponse.Headers.CacheControl?.NoStore);
        var read = (await readResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
        var full = (await fullResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
        Assert.Equal(("Reader", PersonalAccessTokenScopes.Read, true), (read.Token.Name, read.Token.Scope, read.Token.IsActive));
        Assert.Equal(expires.ToUnixTimeMilliseconds(), read.Token.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal((PersonalAccessTokenScopes.Full, (DateTimeOffset?)null), (full.Token.Scope, full.Token.ExpiresAtUtc));
        Assert.StartsWith("kbp_", read.PlaintextToken, StringComparison.Ordinal);

        // As the MCP server does: validate the plaintext, then forward the token's id with each call.
        using var mcp = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        mcp.DefaultRequestHeaders.Add(KanbanInternalAuth.SecretHeaderName, ProgramDependencyWiringTests.ApiApplication.InternalSecret);
        var validatedRead = await ValidateAsync(mcp, read.PlaintextToken);
        var validatedFull = await ValidateAsync(mcp, full.PlaintextToken);
        Assert.Equal((identity.Id, read.Token.Id, PersonalAccessTokenScopes.Read), (validatedRead.AppUserId, validatedRead.PersonalAccessTokenId, validatedRead.Scope));
        Assert.Equal((identity.Id, PersonalAccessTokenScopes.Full), (validatedFull.AppUserId, validatedFull.Scope));

        using var readTokenRead = await ForwardAsync(mcp, validatedRead, HttpMethod.Get, "/api/projects", null);
        using var readTokenWrite = await ForwardAsync(mcp, validatedRead, HttpMethod.Post, "/api/projects", new { name = "Project", key = "READ" });
        using var fullTokenWrite = await ForwardAsync(mcp, validatedFull, HttpMethod.Post, "/api/projects", new { name = "Project", key = "FULL" });
        Assert.Equal(HttpStatusCode.OK, readTokenRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readTokenWrite.StatusCode);
        Assert.Equal(KanbanErrorCodes.ScopeDenied, (await readTokenWrite.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Code);
        Assert.Equal(HttpStatusCode.Created, fullTokenWrite.StatusCode);

        var listed = await admin.GetFromJsonAsync<List<ServiceIdentitySummaryDto>>("/api/admin/service-identities", ProgramDependencyWiringTests.Json);
        Assert.Equal(2, Assert.Single(listed!).ActiveTokenCount);
    }

    [Fact]
    public async Task Issuing_is_refused_with_a_typed_code_for_a_person_an_unknown_id_a_bad_request_or_a_missing_csrf_token()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var person = new AppUser { Issuer = OidcIssuer, Subject = AdminSubject, DisplayName = "The admin", CreatedAtUtc = DateTimeOffset.UtcNow };
        await api.SeedAsync(dbContext => dbContext.AppUsers.AddRange(identity, person));
        using var admin = api.CreateSessionClient(AdminSubject);
        var tokensOf = (Guid id) => $"/api/admin/service-identities/{id}/tokens";

        Assert.Equal(
            [
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument)
            ],
            new[]
            {
                // A person, the admin included, never gets a token through this page.
                await AnswerAsync(admin, tokensOf(person.Id), new { name = "Mine" }),
                await AnswerAsync(admin, tokensOf(Guid.NewGuid()), new { name = "Ghost" }),
                await AnswerAsync(admin, tokensOf(identity.Id), new { name = " " }),
                await AnswerAsync(admin, tokensOf(identity.Id), new { name = new string('n', 161) }),
                await AnswerAsync(admin, tokensOf(identity.Id), new { name = "Bad", scope = "admin" }),
                await AnswerAsync(admin, tokensOf(identity.Id), new { name = "Past", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) })
            });

        using var withoutCsrf = await SendAsync(admin, HttpMethod.Post, tokensOf(identity.Id), new { name = "No CSRF" }, withCsrfToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
        await api.AssertNoTokenAsync();
    }

    [Fact]
    public async Task Issuing_is_refused_with_tokens_disabled_when_personal_access_tokens_are_disabled()
    {
        using var api = new AdminApi(personalAccessTokensEnabled: false);
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var admin = api.CreateSessionClient(AdminSubject);

        Assert.Equal(
            (HttpStatusCode.ServiceUnavailable, KanbanAdminErrorCodes.TokensDisabled),
            await AnswerAsync(admin, $"/api/admin/service-identities/{identity.Id}/tokens", new { name = "Token" }));
        await api.AssertNoTokenAsync();
    }

    [Fact]
    public async Task An_admin_whose_session_carries_the_subject_as_a_name_identifier_is_an_admin()
    {
        // The real OIDC cookie maps inbound claims: the subject arrives as ClaimTypes.NameIdentifier, not "sub".
        using var api = new AdminApi();
        using var client = api.CreateSessionClient(AdminSubject, subjectAsNameIdentifier: true);

        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/me", ProgramDependencyWiringTests.Json);
        using var list = await client.GetAsync("/api/admin/service-identities");

        Assert.True(me!.IsAdmin);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task An_admin_creates_a_service_identity_by_name_and_a_name_that_exists_in_any_case_is_refused()
    {
        using var api = new AdminApi();
        using var admin = api.CreateSessionClient(AdminSubject);

        using var createdResponse = await SendAsync(admin, HttpMethod.Post, "/api/admin/service-identities", new { name = " Dark Factory " }, withCsrfToken: true);

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<ServiceIdentitySummaryDto>(ProgramDependencyWiringTests.Json))!;
        Assert.Equal(("Dark Factory", 0, (DateTimeOffset?)null, false), (created.Name, created.ActiveTokenCount, created.LastUsedAtUtc, created.Disabled));
        var stored = await api.ReadAsync(dbContext => dbContext.AppUsers.AsNoTracking().SingleAsync());
        Assert.Equal((created.Id, ServiceIdentityProvisioner.Issuer, "dark factory", "Dark Factory", (DateTimeOffset?)null),
            (stored.Id, stored.Issuer, stored.Subject, stored.DisplayName, stored.DisabledAtUtc));

        // The name rules of the create-service-identity command: trimmed, 1 to 120 characters; one identity per name in any case.
        Assert.Equal(
            [
                (HttpStatusCode.Conflict, KanbanAdminErrorCodes.IdentityExists),
                (HttpStatusCode.Conflict, KanbanAdminErrorCodes.IdentityExists),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument)
            ],
            new[]
            {
                await AnswerAsync(admin, "/api/admin/service-identities", new { name = "Dark Factory" }),
                await AnswerAsync(admin, "/api/admin/service-identities", new { name = "DARK FACTORY  " }),
                await AnswerAsync(admin, "/api/admin/service-identities", new { name = "   " }),
                await AnswerAsync(admin, "/api/admin/service-identities", new { name = new string('n', ServiceIdentityProvisioner.MaxNameLength + 1) }),
                await AnswerAsync(admin, "/api/admin/service-identities", new { })
            });
        var after = await api.ReadAsync(dbContext => dbContext.AppUsers.AsNoTracking().SingleAsync());
        Assert.Equal((stored.DisplayName, stored.UpdatedAtUtc), (after.DisplayName, after.UpdatedAtUtc));

        using var longest = await SendAsync(admin, HttpMethod.Post, "/api/admin/service-identities",
            new { name = new string('n', ServiceIdentityProvisioner.MaxNameLength) }, withCsrfToken: true);
        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
        // The command finds the identity the page created rather than making a second one.
        Assert.False((await api.ProvisionAsync("dark factory")).Created);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_name_create_it_once_and_refuse_the_rest_with_the_typed_code()
    {
        using var api = new AdminApi();
        using var admin = api.CreateSessionClient(AdminSubject);
        var csrf = (await admin.GetFromJsonAsync<AntiforgeryToken>("/api/auth/antiforgery"))!.Token;

        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/service-identities")
            {
                Content = JsonContent.Create(new { name = index % 2 == 0 ? "Factory" : "FACTORY" })
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            using var response = await admin.SendAsync(request);
            return response.StatusCode == HttpStatusCode.Created
                ? "created"
                : $"{response.StatusCode}:{(await response.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Code}";
        }));

        Assert.Equal(1, answers.Count(answer => answer == "created"));
        Assert.All(answers.Where(answer => answer != "created"), answer => Assert.Equal($"{HttpStatusCode.Conflict}:{KanbanAdminErrorCodes.IdentityExists}", answer));
        Assert.Equal(1, await api.ReadAsync(dbContext => dbContext.AppUsers.CountAsync()));
    }

    [Fact]
    public async Task Disabling_revokes_every_token_and_refuses_issuing_and_enabling_allows_issuing_while_the_old_tokens_stay_revoked()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var other = Identity("Other", DateTimeOffset.UtcNow);
        var revokedEarlier = DateTimeOffset.UtcNow.AddDays(-1);
        var alreadyRevoked = Token(identity, revoked: revokedEarlier);
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.AddRange(identity, other);
            dbContext.PersonalAccessTokens.Add(alreadyRevoked);
        });
        using var admin = api.CreateSessionClient(AdminSubject);
        using var mcp = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        mcp.DefaultRequestHeaders.Add(KanbanInternalAuth.SecretHeaderName, ProgramDependencyWiringTests.ApiApplication.InternalSecret);
        var full = await IssueAsync(admin, identity.Id, "Writer", "full");
        var read = await IssueAsync(admin, identity.Id, "Reader", "read");
        var others = await IssueAsync(admin, other.Id, "Other", "full");
        // As the MCP server holds them in its cache: validated before the disable.
        var validatedFull = await ValidateAsync(mcp, full.PlaintextToken);
        var validatedRead = await ValidateAsync(mcp, read.PlaintextToken);

        using var disableResponse = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/disable", null, withCsrfToken: true);

        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        var disabled = (await disableResponse.Content.ReadFromJsonAsync<ServiceIdentitySummaryDto>(ProgramDependencyWiringTests.Json))!;
        Assert.Equal((identity.Id, true, 0), (disabled.Id, disabled.Disabled, disabled.ActiveTokenCount));
        // Each token is refused through MCP: at the next forwarded call and when it is validated again.
        using (var fullForward = await ForwardAsync(mcp, validatedFull, HttpMethod.Get, "/api/projects", null))
        using (var readForward = await ForwardAsync(mcp, validatedRead, HttpMethod.Get, "/api/projects", null))
        {
            Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (fullForward.StatusCode, readForward.StatusCode));
            Assert.Equal(KanbanErrorCodes.ScopeDenied, (await fullForward.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Code);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, full.PlaintextToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, read.PlaintextToken));
        var tokens = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().ToListAsync());
        Assert.All(tokens.Where(token => token.AppUserId == identity.Id), token => Assert.NotNull(token.RevokedAtUtc));
        Assert.Equal(revokedEarlier.ToUnixTimeMilliseconds(), tokens.Single(token => token.Id == alreadyRevoked.Id).RevokedAtUtc!.Value.ToUnixTimeMilliseconds());
        // Another identity's token is untouched.
        Assert.Null(tokens.Single(token => token.Id == others.Token.Id).RevokedAtUtc);

        // Issuing is refused and writes nothing; a second disable changes nothing.
        Assert.Equal(
            (HttpStatusCode.Conflict, KanbanAdminErrorCodes.IdentityDisabled),
            await AnswerAsync(admin, $"/api/admin/service-identities/{identity.Id}/tokens", new { name = "After disable" }));
        using var disableAgain = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/disable", null, withCsrfToken: true);
        Assert.Equal(HttpStatusCode.OK, disableAgain.StatusCode);
        Assert.Equal(disabled.DisabledAtUtc, (await disableAgain.Content.ReadFromJsonAsync<ServiceIdentitySummaryDto>(ProgramDependencyWiringTests.Json))!.DisabledAtUtc);
        Assert.Equal(4, await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.CountAsync()));
        var listed = await admin.GetFromJsonAsync<List<ServiceIdentitySummaryDto>>("/api/admin/service-identities", ProgramDependencyWiringTests.Json);
        Assert.Equal([("Factory", true), ("Other", false)], listed!.Select(summary => (summary.Name, summary.Disabled)).ToList());
        using (var raw = await admin.GetAsync("/api/admin/service-identities"))
        {
            Assert.Contains("\"disabled\":true", await raw.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var enableResponse = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/enable", null, withCsrfToken: true);

        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);
        var enabled = (await enableResponse.Content.ReadFromJsonAsync<ServiceIdentitySummaryDto>(ProgramDependencyWiringTests.Json))!;
        Assert.Equal((false, (DateTimeOffset?)null, 0), (enabled.Disabled, enabled.DisabledAtUtc, enabled.ActiveTokenCount));
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, full.PlaintextToken));
        using (var oldForward = await ForwardAsync(mcp, validatedFull, HttpMethod.Get, "/api/projects", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, oldForward.StatusCode);
        }

        var issuedAfter = await IssueAsync(admin, identity.Id, "After enable", "full");
        var validatedAfter = await ValidateAsync(mcp, issuedAfter.PlaintextToken);
        using var afterForward = await ForwardAsync(mcp, validatedAfter, HttpMethod.Post, "/api/projects", new { name = "Project", key = "AFTER" });
        Assert.Equal(HttpStatusCode.Created, afterForward.StatusCode);
    }

    [Fact]
    public async Task A_token_whose_owner_is_disabled_is_refused_even_when_it_was_not_revoked()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var admin = api.CreateSessionClient(AdminSubject);
        using var mcp = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        mcp.DefaultRequestHeaders.Add(KanbanInternalAuth.SecretHeaderName, ProgramDependencyWiringTests.ApiApplication.InternalSecret);
        var issued = await IssueAsync(admin, identity.Id, "Writer", "full");
        var validated = await ValidateAsync(mcp, issued.PlaintextToken);

        // As if the token was stored while the disable was committed: the identity is disabled, the token not revoked.
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.Attach(identity);
            identity.DisabledAtUtc = DateTimeOffset.UtcNow;
        });

        using var forward = await ForwardAsync(mcp, validated, HttpMethod.Get, "/api/projects", null);
        Assert.Equal(HttpStatusCode.Forbidden, forward.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, issued.PlaintextToken));
    }

    [Fact]
    public async Task Disable_and_enable_answer_not_found_for_a_person_or_an_unknown_id_and_need_a_csrf_token()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var person = new AppUser { Issuer = OidcIssuer, Subject = AdminSubject, DisplayName = "The admin", CreatedAtUtc = DateTimeOffset.UtcNow };
        await api.SeedAsync(dbContext => dbContext.AppUsers.AddRange(identity, person));
        using var admin = api.CreateSessionClient(AdminSubject);

        foreach (var action in new[] { "disable", "enable" })
        {
            Assert.Equal((HttpStatusCode.NotFound, KanbanErrorCodes.NotFound), await AnswerAsync(admin, $"/api/admin/service-identities/{person.Id}/{action}", new { }));
            Assert.Equal((HttpStatusCode.NotFound, KanbanErrorCodes.NotFound), await AnswerAsync(admin, $"/api/admin/service-identities/{Guid.NewGuid()}/{action}", new { }));
            using var withoutCsrf = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/{action}", null, withCsrfToken: false);
            Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
        }

        using var createWithoutCsrf = await SendAsync(admin, HttpMethod.Post, "/api/admin/service-identities", new { name = "No CSRF" }, withCsrfToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, createWithoutCsrf.StatusCode);
        var users = await api.ReadAsync(dbContext => dbContext.AppUsers.AsNoTracking().ToListAsync());
        Assert.Equal(2, users.Count);
        Assert.All(users, user => Assert.Null(user.DisabledAtUtc));
    }

    // The SQLite tests never see the SQL of MySql.EntityFrameworkCore, which cannot translate every query.
    [Fact]
    public void The_tokens_of_the_service_identities_are_read_by_a_query_mysql_can_translate()
    {
        using var dbContext = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseMySQL("Server=127.0.0.1;Port=3306;Database=kanban_test;User ID=test;Password=test;")
            .Options);

        var sql = KanbanAdminEndpointRouteBuilderExtensions.TokensOfServiceIdentities(dbContext)
            .Select(token => new { token.AppUserId, token.ExpiresAtUtc, token.RevokedAtUtc, token.LastUsedAtUtc })
            .ToQueryString();

        Assert.Contains("INNER JOIN `AppUsers`", sql, StringComparison.Ordinal);
    }

    private static async Task AssertEveryAdminEndpointRefusesAsync(HttpClient client, Guid identityId, bool withCsrfToken)
    {
        HttpResponseMessage[] responses =
        [
            await SendAsync(client, HttpMethod.Get, "/api/admin/service-identities", null, withCsrfToken),
            await SendAsync(client, HttpMethod.Post, $"/api/admin/service-identities/{identityId}/tokens", new { name = "Token", scope = "full" }, withCsrfToken),
            // Refused before the body is read.
            await SendAsync(client, HttpMethod.Post, $"/api/admin/service-identities/{identityId}/tokens", new { name = 1 }, withCsrfToken),
            await SendAsync(client, HttpMethod.Post, "/api/admin/service-identities", new { name = "Intruder" }, withCsrfToken),
            await SendAsync(client, HttpMethod.Post, $"/api/admin/service-identities/{identityId}/disable", null, withCsrfToken),
            await SendAsync(client, HttpMethod.Post, $"/api/admin/service-identities/{identityId}/enable", null, withCsrfToken)
        ];

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
            Assert.Equal(KanbanAdminErrorCodes.AdminRequired, error!.Code);
            Assert.Equal(KanbanAdminEndpointRouteBuilderExtensions.NotAdminMessage, error.Message);
            response.Dispose();
        }
    }

    private static async Task<(HttpStatusCode, string)> AnswerAsync(HttpClient client, string path, object body)
    {
        using var response = await SendAsync(client, HttpMethod.Post, path, body, withCsrfToken: true);
        var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
        return (response.StatusCode, error!.Code);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body, bool withCsrfToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (withCsrfToken)
        {
            var antiforgery = await client.GetFromJsonAsync<AntiforgeryToken>("/api/auth/antiforgery");
            request.Headers.Add("X-CSRF-TOKEN", antiforgery!.Token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<CreatedPersonalAccessTokenDto> IssueAsync(HttpClient admin, Guid identityId, string name, string scope)
    {
        using var response = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identityId}/tokens", new { name, scope }, withCsrfToken: true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
    }

    private static async Task<HttpStatusCode> ValidationStatusAsync(HttpClient mcp, string plaintext)
    {
        using var response = await mcp.PostAsJsonAsync("/api/internal/personal-access-tokens/validate", new ValidatePersonalAccessTokenRequest(plaintext));
        return response.StatusCode;
    }

    private static async Task<ValidatedPersonalAccessTokenDto> ValidateAsync(HttpClient mcp, string plaintext)
    {
        using var response = await mcp.PostAsJsonAsync("/api/internal/personal-access-tokens/validate", new ValidatePersonalAccessTokenRequest(plaintext));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidatedPersonalAccessTokenDto>())!;
    }

    private static Task<HttpResponseMessage> ForwardAsync(HttpClient mcp, ValidatedPersonalAccessTokenDto token, HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add(KanbanInternalAuth.AppUserIdHeaderName, token.AppUserId.ToString());
        request.Headers.Add(KanbanInternalAuth.PersonalAccessTokenIdHeaderName, token.PersonalAccessTokenId.ToString());
        return mcp.SendAsync(request);
    }

    private static AppUser Identity(string name, DateTimeOffset createdAtUtc) => new()
    {
        Issuer = ServiceIdentityProvisioner.Issuer,
        Subject = name.ToLowerInvariant(),
        DisplayName = name,
        CreatedAtUtc = createdAtUtc,
        UpdatedAtUtc = createdAtUtc
    };

    private static PersonalAccessToken Token(AppUser owner, DateTimeOffset? lastUsed = null, DateTimeOffset? revoked = null, DateTimeOffset? expires = null) => new()
    {
        AppUserId = owner.Id,
        Name = "token",
        TokenPrefix = "prefix",
        TokenHash = Guid.NewGuid().ToString("N"),
        EncryptedSecret = "unused",
        CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
        LastUsedAtUtc = lastUsed,
        RevokedAtUtc = revoked,
        ExpiresAtUtc = expires
    };

    private sealed record AntiforgeryToken(string Token);

    /// <summary>The real application with authentication enabled and a test scheme in place of the OIDC cookie.</summary>
    private sealed class AdminApi : IDisposable
    {
        private readonly ProgramDependencyWiringTests.ApiApplication inner = new();
        private readonly WebApplicationFactory<KanbanDbContext> factory;

        public AdminApi(string? adminSubjects = AdminSubject, bool personalAccessTokensEnabled = true)
        {
            factory = inner.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("Auth:Enabled", "true");
                builder.UseSetting("Auth:Authority", OidcIssuer);
                builder.UseSetting("Auth:ClientId", "kanban-board");
                builder.UseSetting("Auth:ClientSecret", "secret");
                builder.UseSetting("Auth:AdminSubjects", adminSubjects ?? string.Empty);
                builder.UseSetting("PersonalAccessTokens:Enabled", personalAccessTokensEnabled ? "true" : "false");
                builder.UseSetting("PersonalAccessTokens:EncryptionKey", Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestSessionHandler>(TestSessionHandler.SchemeName, null);
                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultScheme = TestSessionHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestSessionHandler.SchemeName;
                    });
                });
            });
        }

        /// <summary>
        /// A browser session signed in as <paramref name="subject"/>, over https so the antiforgery cookie is sent.
        /// With <paramref name="subjectAsNameIdentifier"/> the subject is carried as the real OIDC cookie carries it.
        /// </summary>
        public HttpClient CreateSessionClient(string subject, bool subjectAsNameIdentifier = false)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Add(SubjectHeader, subject);
            if (subjectAsNameIdentifier)
            {
                client.DefaultRequestHeaders.Add(NameIdentifierHeader, "true");
            }

            return client;
        }

        public async Task<T> ReadAsync<T>(Func<KanbanDbContext, Task<T>> read)
        {
            await using var dbContext = inner.OpenDatabase();
            return await read(dbContext);
        }

        /// <summary>Runs the create-service-identity command's provisioning against the application's database.</summary>
        public async Task<ProvisionedServiceIdentity> ProvisionAsync(string name)
        {
            using var scope = factory.Services.CreateScope();
            return await ServiceIdentityProvisioner.ProvisionAsync(
                scope.ServiceProvider.GetRequiredService<KanbanDbContext>(),
                scope.ServiceProvider.GetRequiredService<IPersonalAccessTokenService>(),
                name,
                rotate: false,
                CancellationToken.None);
        }

        public HttpClient CreateClient(WebApplicationFactoryClientOptions options) => factory.CreateClient(options);

        public HttpClient CreateCallerClient()
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var user = new AppUser { Issuer = "issuer", Subject = "caller" };
            var token = new PersonalAccessToken { AppUserId = user.Id, Name = "token", TokenPrefix = "kbp", TokenHash = Guid.NewGuid().ToString("N"), EncryptedSecret = "unused" };
            SeedAsync(dbContext =>
            {
                dbContext.AppUsers.Add(user);
                dbContext.PersonalAccessTokens.Add(token);
            }).GetAwaiter().GetResult();
            client.DefaultRequestHeaders.Add(KanbanInternalAuth.SecretHeaderName, ProgramDependencyWiringTests.ApiApplication.InternalSecret);
            client.DefaultRequestHeaders.Add(KanbanInternalAuth.AppUserIdHeaderName, user.Id.ToString());
            client.DefaultRequestHeaders.Add(KanbanInternalAuth.PersonalAccessTokenIdHeaderName, token.Id.ToString());
            return client;
        }

        public async Task SeedAsync(Action<KanbanDbContext> seed)
        {
            // The application creates the database when it starts.
            _ = factory.Server;
            await using var dbContext = inner.OpenDatabase();
            seed(dbContext);
            await dbContext.SaveChangesAsync();
        }

        public async Task AssertNoTokenAsync(int exceptCallerTokens = 0)
        {
            await using var dbContext = inner.OpenDatabase();
            Assert.Equal(exceptCallerTokens, await dbContext.PersonalAccessTokens.CountAsync());
        }

        public void Dispose()
        {
            factory.Dispose();
            inner.Dispose();
        }
    }

    private sealed class TestSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestSession";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SubjectHeader, out var subject) || string.IsNullOrWhiteSpace(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var subjectClaimType = Request.Headers.ContainsKey(NameIdentifierHeader) ? ClaimTypes.NameIdentifier : "sub";
            var identity = new ClaimsIdentity(
                [
                    new Claim(subjectClaimType, subject!, ClaimValueTypes.String, OidcIssuer),
                    new Claim("name", $"User {subject}", ClaimValueTypes.String, OidcIssuer)
                ],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
