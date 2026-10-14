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
        // The refusal names the identity the name clashed with, not the name asked for.
        using (var clash = await SendAsync(admin, HttpMethod.Post, "/api/admin/service-identities", new { name = "DARK factory" }, withCsrfToken: true))
        {
            var message = (await clash.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Message;
            Assert.Equal($"A service identity named 'Dark Factory' ({stored.Id}) already exists; 'DARK factory' is the same name.", message);
        }

        using var longest = await SendAsync(admin, HttpMethod.Post, "/api/admin/service-identities",
            new { name = new string('n', ServiceIdentityProvisioner.MaxNameLength) }, withCsrfToken: true);
        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
        // The command finds the identity the page created rather than making a second one.
        Assert.False((await api.ProvisionAsync("dark factory")).Created);
        // And keeps the name the page gave it.
        Assert.Equal("Dark Factory", (await api.ReadAsync(dbContext => dbContext.AppUsers.AsNoTracking().SingleAsync(user => user.Id == stored.Id))).DisplayName);
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
        Assert.Equal(KanbanInternalApiExtensions.UnknownTokenMessage, (await forward.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, issued.PlaintextToken));
        // The list counts none of its tokens as active.
        var listed = await admin.GetFromJsonAsync<List<ServiceIdentitySummaryDto>>("/api/admin/service-identities", ProgramDependencyWiringTests.Json);
        Assert.Equal((true, 0), (Assert.Single(listed!).Disabled, listed![0].ActiveTokenCount));
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

    [Fact]
    public async Task The_token_list_of_an_identity_shows_each_tokens_name_scope_dates_and_state_and_no_secret()
    {
        using var api = new AdminApi();
        var now = DateTimeOffset.UtcNow;
        var identity = Identity("Factory", now);
        var other = Identity("Other", now);
        var person = new AppUser { Issuer = OidcIssuer, Subject = "person", DisplayName = "A person", CreatedAtUtc = now };
        var active = Token(identity, lastUsed: now.AddHours(-1), expires: now.AddDays(1), name: "Active", scope: "read", created: now.AddDays(-1));
        var expired = Token(identity, expires: now.AddMinutes(-1), name: "Expired", created: now.AddDays(-2));
        // Revoked wins over expired.
        var revoked = Token(identity, revoked: now.AddMinutes(-30), expires: now.AddMinutes(-1), name: "Revoked", created: now.AddDays(-3));
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.AddRange(identity, other, person);
            dbContext.PersonalAccessTokens.AddRange(active, expired, revoked, Token(other), Token(person));
        });
        using var admin = api.CreateSessionClient(AdminSubject);

        using var response = await admin.GetAsync($"/api/admin/service-identities/{identity.Id}/tokens");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var listed = System.Text.Json.JsonSerializer.Deserialize<List<ServiceIdentityTokenDto>>(json, ProgramDependencyWiringTests.Json)!;
        Assert.Equal(
            [
                (active.Id, "Active", "read", PersonalAccessTokenStates.Active, active.LastUsedAtUtc!.Value.ToUnixTimeMilliseconds(), (long?)active.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds(), (long?)null),
                (expired.Id, "Expired", "full", PersonalAccessTokenStates.Expired, 0L, (long?)expired.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds(), (long?)null),
                (revoked.Id, "Revoked", "full", PersonalAccessTokenStates.Revoked, 0L, (long?)revoked.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds(), (long?)revoked.RevokedAtUtc!.Value.ToUnixTimeMilliseconds())
            ],
            listed.Select(token => (token.Id, token.Name, token.Scope, token.State, token.LastUsedAtUtc?.ToUnixTimeMilliseconds() ?? 0L,
                token.ExpiresAtUtc?.ToUnixTimeMilliseconds(), token.RevokedAtUtc?.ToUnixTimeMilliseconds())).ToList());
        Assert.Equal(active.CreatedAtUtc.ToUnixTimeMilliseconds(), listed[0].CreatedAtUtc.ToUnixTimeMilliseconds());
        // No secret material: neither the lookup hash nor the encrypted secret.
        Assert.DoesNotContain(active.TokenHash, json, StringComparison.Ordinal);
        Assert.DoesNotContain(active.EncryptedSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext", json, StringComparison.OrdinalIgnoreCase);

        // A person's id or an unknown one lists nothing.
        foreach (var id in new[] { person.Id, Guid.NewGuid() })
        {
            using var refused = await admin.GetAsync($"/api/admin/service-identities/{id}/tokens");
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal(KanbanErrorCodes.NotFound, (await refused.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Code);
        }
    }

    [Fact]
    public async Task A_revoked_token_is_refused_through_the_mcp_path_and_revoking_again_changes_nothing()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var other = Identity("Other", DateTimeOffset.UtcNow);
        var person = new AppUser { Issuer = OidcIssuer, Subject = "person", DisplayName = "A person", CreatedAtUtc = DateTimeOffset.UtcNow };
        var personsToken = Token(person);
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.AddRange(identity, other, person);
            dbContext.PersonalAccessTokens.Add(personsToken);
        });
        using var admin = api.CreateSessionClient(AdminSubject);
        using var mcp = api.CreateMcpClient();
        var issued = await IssueAsync(admin, identity.Id, "Writer", "full");
        var others = await IssueAsync(admin, other.Id, "Other", "full");
        // As the MCP server holds it in its cache: validated before the revoke.
        var validated = await ValidateAsync(mcp, issued.PlaintextToken);
        var revokePath = $"/api/admin/service-identities/{identity.Id}/tokens/{issued.Token.Id}";

        using (var withoutCsrf = await SendAsync(admin, HttpMethod.Delete, revokePath, null, withCsrfToken: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
        }

        using (var revoke = await SendAsync(admin, HttpMethod.Delete, revokePath, null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using (var forward = await ForwardAsync(mcp, validated, HttpMethod.Get, "/api/projects", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forward.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, issued.PlaintextToken));
        var revokedAt = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().SingleAsync(token => token.Id == issued.Token.Id));
        Assert.NotNull(revokedAt.RevokedAtUtc);
        var listed = await admin.GetFromJsonAsync<List<ServiceIdentityTokenDto>>($"/api/admin/service-identities/{identity.Id}/tokens", ProgramDependencyWiringTests.Json);
        Assert.Equal(PersonalAccessTokenStates.Revoked, Assert.Single(listed!).State);

        // Revoking a revoked token succeeds and keeps the time it was first revoked.
        using (var again = await SendAsync(admin, HttpMethod.Delete, revokePath, null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        var revokedAgain = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().SingleAsync(token => token.Id == issued.Token.Id));
        Assert.Equal(revokedAt.RevokedAtUtc, revokedAgain.RevokedAtUtc);

        // Another identity's token, a person's token (under the identity or under the person) and an unknown one: 404.
        foreach (var path in new[]
        {
            $"/api/admin/service-identities/{identity.Id}/tokens/{others.Token.Id}",
            $"/api/admin/service-identities/{identity.Id}/tokens/{personsToken.Id}",
            $"/api/admin/service-identities/{person.Id}/tokens/{personsToken.Id}",
            $"/api/admin/service-identities/{identity.Id}/tokens/{Guid.NewGuid()}"
        })
        {
            using var refused = await SendAsync(admin, HttpMethod.Delete, path, null, withCsrfToken: true);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal(KanbanErrorCodes.NotFound, (await refused.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Code);
        }

        var untouched = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking()
            .Where(token => token.Id == others.Token.Id || token.Id == personsToken.Id).ToListAsync());
        Assert.Equal(2, untouched.Count);
        Assert.All(untouched, token => Assert.Null(token.RevokedAtUtc));
    }

    [Fact]
    public async Task Rotating_issues_a_replacement_of_the_same_name_and_scope_shown_once_and_revokes_the_old_token()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var admin = api.CreateSessionClient(AdminSubject);
        using var mcp = api.CreateMcpClient();
        var old = await IssueAsync(admin, identity.Id, "Reader", "read");
        var validatedOld = await ValidateAsync(mcp, old.PlaintextToken);
        var expires = DateTimeOffset.UtcNow.AddDays(7);

        using var response = await SendAsync(admin, HttpMethod.Post, RotatePath(identity.Id, old.Token.Id), new { expiresAtUtc = expires }, withCsrfToken: true);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var rotated = (await response.Content.ReadFromJsonAsync<RotatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
        Assert.Equal((old.Token.Id, "Reader", PersonalAccessTokenScopes.Read, true), (rotated.RevokedTokenId, rotated.Token.Name, rotated.Token.Scope, rotated.Token.IsActive));
        Assert.NotEqual(old.Token.Id, rotated.Token.Id);
        Assert.Equal(expires.ToUnixTimeMilliseconds(), rotated.Token.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.StartsWith("kbp_", rotated.PlaintextToken, StringComparison.Ordinal);

        // The replacement works through MCP with its scope; the old token is refused.
        var validatedNew = await ValidateAsync(mcp, rotated.PlaintextToken);
        Assert.Equal((identity.Id, PersonalAccessTokenScopes.Read), (validatedNew.AppUserId, validatedNew.Scope));
        using (var newForward = await ForwardAsync(mcp, validatedNew, HttpMethod.Get, "/api/projects", null))
        using (var oldForward = await ForwardAsync(mcp, validatedOld, HttpMethod.Get, "/api/projects", null))
        {
            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Forbidden), (newForward.StatusCode, oldForward.StatusCode));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, old.PlaintextToken));
        var listed = await admin.GetFromJsonAsync<List<ServiceIdentityTokenDto>>($"/api/admin/service-identities/{identity.Id}/tokens", ProgramDependencyWiringTests.Json);
        Assert.Equal(
            [(rotated.Token.Id, PersonalAccessTokenStates.Active), (old.Token.Id, PersonalAccessTokenStates.Revoked)],
            listed!.OrderBy(token => token.Id == old.Token.Id).Select(token => (token.Id, token.State)).ToList());

        // The old token, now revoked, is not rotated again; nothing is written.
        Assert.Equal((HttpStatusCode.Conflict, KanbanAdminErrorCodes.TokenRevoked), await AnswerAsync(admin, RotatePath(identity.Id, old.Token.Id), new { }));
        Assert.Equal(2, await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.CountAsync()));

        // Without a body, or without an expiry, the replacement does not expire, even when the rotated token did.
        using (var noBody = await SendAsync(admin, HttpMethod.Post, RotatePath(identity.Id, rotated.Token.Id), null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.Created, noBody.StatusCode);
            var replacement = (await noBody.Content.ReadFromJsonAsync<RotatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
            Assert.Equal(("Reader", (DateTimeOffset?)null), (replacement.Token.Name, replacement.Token.ExpiresAtUtc));
        }
    }

    [Fact]
    public async Task An_expired_token_is_rotated()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var expired = Token(identity, expires: DateTimeOffset.UtcNow.AddMinutes(-1), name: "Nightly", scope: "read");
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.Add(identity);
            dbContext.PersonalAccessTokens.Add(expired);
        });
        using var admin = api.CreateSessionClient(AdminSubject);

        using var response = await SendAsync(admin, HttpMethod.Post, RotatePath(identity.Id, expired.Id), new { }, withCsrfToken: true);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rotated = (await response.Content.ReadFromJsonAsync<RotatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
        Assert.Equal(("Nightly", PersonalAccessTokenScopes.Read, true), (rotated.Token.Name, rotated.Token.Scope, rotated.Token.IsActive));
        Assert.NotNull((await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().SingleAsync(token => token.Id == expired.Id))).RevokedAtUtc);
    }

    [Fact]
    public async Task A_refused_or_failed_rotate_writes_nothing_and_the_old_token_keeps_working()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var other = Identity("Other", DateTimeOffset.UtcNow);
        var disabled = Identity("Disabled", DateTimeOffset.UtcNow);
        disabled.DisabledAtUtc = DateTimeOffset.UtcNow;
        var person = new AppUser { Issuer = OidcIssuer, Subject = "person", DisplayName = "A person", CreatedAtUtc = DateTimeOffset.UtcNow };
        var personsToken = Token(person);
        var disabledsToken = Token(disabled);
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.AddRange(identity, other, disabled, person);
            dbContext.PersonalAccessTokens.AddRange(personsToken, disabledsToken);
        });
        using var admin = api.CreateSessionClient(AdminSubject);
        using var mcp = api.CreateMcpClient();
        var old = await IssueAsync(admin, identity.Id, "Writer", "full");
        var others = await IssueAsync(admin, other.Id, "Other", "full");

        Assert.Equal(
            [
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.Conflict, KanbanAdminErrorCodes.IdentityDisabled)
            ],
            new[]
            {
                await AnswerAsync(admin, RotatePath(identity.Id, old.Token.Id), new { expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }),
                // Another identity's token, a person's token (under the identity or the person), unknown ids.
                await AnswerAsync(admin, RotatePath(identity.Id, others.Token.Id), new { }),
                await AnswerAsync(admin, RotatePath(identity.Id, personsToken.Id), new { }),
                await AnswerAsync(admin, RotatePath(person.Id, personsToken.Id), new { }),
                await AnswerAsync(admin, RotatePath(identity.Id, Guid.NewGuid()), new { }),
                await AnswerAsync(admin, RotatePath(Guid.NewGuid(), old.Token.Id), new { }),
                await AnswerAsync(admin, RotatePath(disabled.Id, disabledsToken.Id), new { })
            });
        using (var withoutCsrf = await SendAsync(admin, HttpMethod.Post, RotatePath(identity.Id, old.Token.Id), new { }, withCsrfToken: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
        }

        // Storing the replacement fails (the test server rethrows what the request threw): the old token is not revoked.
        api.FailNextSave = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => SendAsync(admin, HttpMethod.Post, RotatePath(identity.Id, old.Token.Id), new { }, withCsrfToken: true));

        var tokens = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().ToListAsync());
        Assert.Equal(4, tokens.Count);
        Assert.All(tokens, token => Assert.Null(token.RevokedAtUtc));
        var validated = await ValidateAsync(mcp, old.PlaintextToken);
        using var forward = await ForwardAsync(mcp, validated, HttpMethod.Get, "/api/projects", null);
        Assert.Equal(HttpStatusCode.OK, forward.StatusCode);
    }

    [Fact]
    public async Task Rotating_is_refused_with_tokens_disabled_and_the_old_token_is_not_revoked()
    {
        using var api = new AdminApi(personalAccessTokensEnabled: false);
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var token = Token(identity);
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.Add(identity);
            dbContext.PersonalAccessTokens.Add(token);
        });
        using var admin = api.CreateSessionClient(AdminSubject);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, KanbanAdminErrorCodes.TokensDisabled), await AnswerAsync(admin, RotatePath(identity.Id, token.Id), new { }));
        var stored = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().SingleAsync());
        Assert.Equal((token.Id, (DateTimeOffset?)null), (stored.Id, stored.RevokedAtUtc));
    }

    [Fact]
    public async Task Concurrent_rotates_of_one_token_leave_exactly_one_active_replacement_and_refuse_the_rest_with_the_typed_code()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var admin = api.CreateSessionClient(AdminSubject);
        var old = await IssueAsync(admin, identity.Id, "Writer", "full");
        var csrf = (await admin.GetFromJsonAsync<AntiforgeryToken>("/api/auth/antiforgery"))!.Token;

        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RotatePath(identity.Id, old.Token.Id)) { Content = JsonContent.Create(new { }) };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            using var response = await admin.SendAsync(request);
            return response.StatusCode == HttpStatusCode.Created
                ? "rotated"
                : $"{response.StatusCode}:{(await response.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Code}";
        }));

        Assert.Equal(1, answers.Count(answer => answer == "rotated"));
        Assert.All(answers.Where(answer => answer != "rotated"), answer => Assert.Equal($"{HttpStatusCode.Conflict}:{KanbanAdminErrorCodes.TokenRevoked}", answer));
        var tokens = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().ToListAsync());
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens.Single(token => token.Id == old.Token.Id).RevokedAtUtc);
        Assert.Null(tokens.Single(token => token.Id != old.Token.Id).RevokedAtUtc);
    }

    [Fact]
    public async Task Each_token_issued_rotated_revoked_or_revoked_by_a_disable_records_the_acting_admin_and_the_list_shows_who()
    {
        const string SecondAdmin = "second-admin";
        using var api = new AdminApi($"{AdminSubject} {SecondAdmin}");
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var first = api.CreateSessionClient(AdminSubject);
        using var second = api.CreateSessionClient(SecondAdmin);
        // Their user rows, as the page creates them when it loads.
        var firstUser = (await first.GetFromJsonAsync<CurrentUserDto>("/api/me", ProgramDependencyWiringTests.Json))!;
        var secondUser = (await second.GetFromJsonAsync<CurrentUserDto>("/api/me", ProgramDependencyWiringTests.Json))!;
        var firstAdmin = new PersonalAccessTokenAdminDto(firstUser.Id, $"User {AdminSubject}");
        var secondAdmin = new PersonalAccessTokenAdminDto(secondUser.Id, $"User {SecondAdmin}");

        var writer = await IssueAsync(first, identity.Id, "Writer", "full");
        var reader = await IssueAsync(first, identity.Id, "Reader", "read");
        // The second admin rotates the writer, the first revokes its replacement, the second revokes it again.
        using var rotateResponse = await SendAsync(second, HttpMethod.Post, RotatePath(identity.Id, writer.Token.Id), new { }, withCsrfToken: true);
        var replacement = (await rotateResponse.Content.ReadFromJsonAsync<RotatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
        var replacementPath = $"/api/admin/service-identities/{identity.Id}/tokens/{replacement.Token.Id}";
        using (var revoke = await SendAsync(first, HttpMethod.Delete, replacementPath, null, withCsrfToken: true))
        using (var revokeAgain = await SendAsync(second, HttpMethod.Delete, replacementPath, null, withCsrfToken: true))
        {
            Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (revoke.StatusCode, revokeAgain.StatusCode));
        }

        // The second admin disables the identity: it revokes the reader, and the tokens revoked before keep their revoker.
        using (var disable = await SendAsync(second, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/disable", null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }

        using var response = await first.GetAsync($"/api/admin/service-identities/{identity.Id}/tokens");
        var json = await response.Content.ReadAsStringAsync();
        var listed = System.Text.Json.JsonSerializer.Deserialize<List<ServiceIdentityTokenDto>>(json, ProgramDependencyWiringTests.Json)!;
        Assert.Equal(
            [
                (writer.Token.Id, firstAdmin, secondAdmin),
                (reader.Token.Id, firstAdmin, secondAdmin),
                (replacement.Token.Id, secondAdmin, firstAdmin)
            ],
            listed.OrderBy(token => token.Id == replacement.Token.Id).ThenBy(token => token.Name != "Writer")
                .Select(token => (token.Id, token.IssuedBy, token.RevokedBy)).ToList());
        Assert.Contains($"\"issuedBy\":{{\"id\":\"{firstUser.Id}\",\"name\":\"User {AdminSubject}\"}}", json, StringComparison.Ordinal);
        Assert.Contains($"\"revokedBy\":{{\"id\":\"{secondUser.Id}\",\"name\":\"User {SecondAdmin}\"}}", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_of_the_command_of_the_settings_page_or_from_before_shows_no_admin_and_still_works()
    {
        using var api = new AdminApi();
        var legacy = Identity("Legacy", DateTimeOffset.UtcNow);
        var legacyRevoked = Token(legacy, name: "Revoked before", revoked: DateTimeOffset.UtcNow.AddDays(-1));
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.Add(legacy);
            dbContext.PersonalAccessTokens.Add(legacyRevoked);
        });
        // As a token stored before the admin columns existed: written with none of them set.
        var legacyToken = await api.CreateTokenAsync(legacy.Id, "From before");
        using var admin = api.CreateSessionClient(AdminSubject);
        using var mcp = api.CreateMcpClient();

        // The command issues a pair, then rotates it: neither the pairs nor the revocations name an admin.
        var issued = await api.ProvisionAsync("Factory");
        var rotated = await api.ProvisionAsync("Factory", rotate: true);
        // The admin's own token, issued and revoked on the Settings page, as its owner.
        using var createOwn = await SendAsync(admin, HttpMethod.Post, "/api/me/personal-access-tokens", new { name = "Mine" }, withCsrfToken: true);
        Assert.Equal(HttpStatusCode.Created, createOwn.StatusCode);
        var own = (await createOwn.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
        using (var revokeOwn = await SendAsync(admin, HttpMethod.Delete, $"/api/me/personal-access-tokens/{own.Token.Id}", null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, revokeOwn.StatusCode);
        }

        var tokens = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().ToListAsync());
        Assert.Equal(7, tokens.Count);
        Assert.Equal(4, tokens.Count(token => token.RevokedAtUtc is not null));
        Assert.All(tokens, token => Assert.Equal(
            (token.Id, (Guid?)null, (string?)null, (Guid?)null, (string?)null),
            (token.Id, token.IssuedByUserId, token.IssuedByName, token.RevokedByUserId, token.RevokedByName)));
        foreach (var identityId in new[] { legacy.Id, rotated.User.Id })
        {
            var listed = await admin.GetFromJsonAsync<List<ServiceIdentityTokenDto>>($"/api/admin/service-identities/{identityId}/tokens", ProgramDependencyWiringTests.Json);
            Assert.All(listed!, token => Assert.Equal((token.Name, (PersonalAccessTokenAdminDto?)null, (PersonalAccessTokenAdminDto?)null), (token.Name, token.IssuedBy, token.RevokedBy)));
        }

        // The Settings page's list of the user's own tokens is unchanged: it names no admin.
        using (var ownList = await admin.GetAsync("/api/me/personal-access-tokens"))
        {
            var ownJson = await ownList.Content.ReadAsStringAsync();
            Assert.Contains("\"tokenPrefix\"", ownJson, StringComparison.Ordinal);
            Assert.DoesNotContain("issuedBy", ownJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("revokedBy", ownJson, StringComparison.OrdinalIgnoreCase);
        }

        // A token from before still works through MCP, as do the command's new pair; the rotated-away pair does not.
        var validatedLegacy = await ValidateAsync(mcp, legacyToken.PlaintextToken);
        using (var forward = await ForwardAsync(mcp, validatedLegacy, HttpMethod.Get, "/api/projects", null))
        {
            Assert.Equal(HttpStatusCode.OK, forward.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, await ValidationStatusAsync(mcp, rotated.FullToken.PlaintextToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidationStatusAsync(mcp, issued.FullToken.PlaintextToken));
    }

    // Every "now" of the admin endpoints is the application's clock: here a day behind the real one.
    [Fact]
    public async Task The_admin_endpoints_judge_expiry_by_the_applications_clock()
    {
        var clock = new WorkItemClaimApiTests.MutableClock();
        var realNow = DateTimeOffset.UtcNow;
        clock.Advance(realNow.AddDays(-1) - clock.Now);
        using var api = new AdminApi(clock: clock);
        var identity = Identity("Factory", realNow.AddDays(-2));
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var admin = api.CreateSessionClient(AdminSubject);
        // In the past by the real clock, in the future by the application's.
        var expires = realNow.AddHours(-1);

        using var issueResponse = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/tokens",
            new { name = "Writer", expiresAtUtc = expires }, withCsrfToken: true);
        Assert.Equal(HttpStatusCode.Created, issueResponse.StatusCode);
        var issued = (await issueResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!;
        Assert.True(issued.Token.IsActive);

        using var rotateResponse = await SendAsync(admin, HttpMethod.Post, RotatePath(identity.Id, issued.Token.Id),
            new { expiresAtUtc = realNow.AddMinutes(-30) }, withCsrfToken: true);
        Assert.Equal(HttpStatusCode.Created, rotateResponse.StatusCode);
        Assert.True((await rotateResponse.Content.ReadFromJsonAsync<RotatedPersonalAccessTokenDto>(ProgramDependencyWiringTests.Json))!.Token.IsActive);

        var listed = await admin.GetFromJsonAsync<List<ServiceIdentitySummaryDto>>("/api/admin/service-identities", ProgramDependencyWiringTests.Json);
        Assert.Equal(1, Assert.Single(listed!).ActiveTokenCount);

        // Now a day ahead of the real clock: an expiry in the real future is refused, and the token has expired.
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(
            (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
            await AnswerAsync(admin, $"/api/admin/service-identities/{identity.Id}/tokens", new { name = "Late", expiresAtUtc = realNow.AddDays(1) }));
        var tokens = await admin.GetFromJsonAsync<List<ServiceIdentityTokenDto>>($"/api/admin/service-identities/{identity.Id}/tokens", ProgramDependencyWiringTests.Json);
        var active = tokens!.Single(token => token.RevokedAtUtc is null);
        Assert.Equal(
            (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
            await AnswerAsync(admin, RotatePath(identity.Id, active.Id), new { expiresAtUtc = realNow.AddDays(1) }));
        listed = await admin.GetFromJsonAsync<List<ServiceIdentitySummaryDto>>("/api/admin/service-identities", ProgramDependencyWiringTests.Json);
        Assert.Equal(0, Assert.Single(listed!).ActiveTokenCount);

        // The times stored are the application's too: a revoke, then a disable, each an hour on.
        clock.Advance(TimeSpan.FromHours(1));
        var revokedAt = clock.Now;
        using (var revoke = await SendAsync(admin, HttpMethod.Delete, $"/api/admin/service-identities/{identity.Id}/tokens/{active.Id}", null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        clock.Advance(TimeSpan.FromHours(1));
        var lastIssuedAt = clock.Now;
        var last = await IssueAsync(admin, identity.Id, "Last", "full");
        clock.Advance(TimeSpan.FromHours(1));
        var disabledAt = clock.Now;
        using (var disable = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{identity.Id}/disable", null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }

        var stored = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().ToListAsync());
        var issuedAt = realNow.AddDays(-1);
        Assert.Equal(
            [
                (issued.Token.Id, issuedAt, (DateTimeOffset?)issuedAt),
                (active.Id, issuedAt, (DateTimeOffset?)revokedAt),
                (last.Token.Id, lastIssuedAt, (DateTimeOffset?)disabledAt)
            ],
            stored.OrderBy(token => token.CreatedAtUtc).ThenBy(token => token.Id != issued.Token.Id)
                .Select(token => (token.Id, token.CreatedAtUtc, token.RevokedAtUtc)).ToList());
        Assert.All(stored.Where(token => token.ExpiresAtUtc is not null), token => Assert.True(token.ExpiresAtUtc > token.CreatedAtUtc));
    }

    // A create, an enable and the command's run record the application's clock too: here a day behind the real one.
    [Fact]
    public async Task Create_enable_and_the_command_store_the_applications_clock()
    {
        var clock = new WorkItemClaimApiTests.MutableClock();
        clock.Advance(DateTimeOffset.UtcNow.AddDays(-1) - clock.Now);
        using var api = new AdminApi(clock: clock);
        using var admin = api.CreateSessionClient(AdminSubject);

        var createdAt = clock.Now;
        using (var create = await SendAsync(admin, HttpMethod.Post, "/api/admin/service-identities", new { name = "Factory" }, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        var id = (await api.ReadAsync(dbContext => dbContext.AppUsers.AsNoTracking().SingleAsync())).Id;
        clock.Advance(TimeSpan.FromHours(1));
        using (var disable = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{id}/disable", null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }

        clock.Advance(TimeSpan.FromHours(1));
        var enabledAt = clock.Now;
        using (var enable = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-identities/{id}/enable", null, withCsrfToken: true))
        {
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        }

        var stored = await api.ReadAsync(dbContext => dbContext.AppUsers.AsNoTracking().SingleAsync());
        Assert.Equal((createdAt, enabledAt), (stored.CreatedAtUtc, stored.UpdatedAtUtc));

        clock.Advance(TimeSpan.FromHours(1));
        var provisionedAt = clock.Now;
        var provisioned = await api.ProvisionAsync("Command");
        stored = await api.ReadAsync(dbContext => dbContext.AppUsers.AsNoTracking().SingleAsync(user => user.Id == provisioned.User.Id));
        Assert.Equal((provisionedAt, provisionedAt), (stored.CreatedAtUtc, stored.UpdatedAtUtc));
    }

    // In Production, as the server runs: no developer exception page answers for the endpoint.
    [Fact]
    public async Task A_malformed_wrong_typed_or_empty_body_is_refused_with_a_typed_code_writes_nothing_and_logs_no_error()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        var token = Token(identity, name: "Writer");
        await api.SeedAsync(dbContext =>
        {
            dbContext.AppUsers.Add(identity);
            dbContext.PersonalAccessTokens.Add(token);
        });
        using var admin = api.CreateSessionClient(AdminSubject);
        var create = "/api/admin/service-identities";
        var issue = $"/api/admin/service-identities/{identity.Id}/tokens";
        var rotate = RotatePath(identity.Id, token.Id);

        var answers = new List<(string Path, string Body, HttpStatusCode Status, string? Code, bool HasMessage)>();
        foreach (var (path, body) in new[]
        {
            (create, "{"), (create, "{\"name\": 1}"), (create, "[]"), (create, ""), (create, "null"),
            (issue, "{"), (issue, "{\"name\": \"Token\", \"expiresAtUtc\": \"tomorrow\"}"), (issue, "{\"name\": {}}"), (issue, ""), (issue, "null"),
            (rotate, "{"), (rotate, "{\"expiresAtUtc\": \"tomorrow\"}"), (rotate, "{\"expiresAtUtc\": 5}"), (rotate, "[1]")
        })
        {
            using var response = await SendRawAsync(admin, path, body);
            var text = await response.Content.ReadAsStringAsync();
            var error = text.Length == 0 ? null : System.Text.Json.JsonSerializer.Deserialize<KanbanErrorDto>(text, ProgramDependencyWiringTests.Json);
            answers.Add((path, body, response.StatusCode, error?.Code, !string.IsNullOrWhiteSpace(error?.Message)));
        }

        Assert.All(answers, answer => Assert.Equal((answer.Path, answer.Body, HttpStatusCode.BadRequest, (string?)KanbanErrorCodes.InvalidArgument, true), answer));
        Assert.Equal(1, await api.ReadAsync(dbContext => dbContext.AppUsers.CountAsync()));
        var stored = await api.ReadAsync(dbContext => dbContext.PersonalAccessTokens.AsNoTracking().SingleAsync());
        Assert.Null(stored.RevokedAtUtc);
        Assert.Empty(api.Logs.Entries.Where(entry => entry.Level >= LogLevel.Error));
    }

    // Disable and enable read no body: whatever is sent is ignored and nothing is logged as an error.
    [Fact]
    public async Task Disable_and_enable_ignore_a_malformed_body_and_log_no_error()
    {
        using var api = new AdminApi();
        var identity = Identity("Factory", DateTimeOffset.UtcNow);
        await api.SeedAsync(dbContext => dbContext.AppUsers.Add(identity));
        using var admin = api.CreateSessionClient(AdminSubject);

        using var disable = await SendRawAsync(admin, $"/api/admin/service-identities/{identity.Id}/disable", "{");
        using var enable = await SendRawAsync(admin, $"/api/admin/service-identities/{identity.Id}/enable", "{\"x\": 1");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (disable.StatusCode, enable.StatusCode));
        Assert.Empty(api.Logs.Entries.Where(entry => entry.Level >= LogLevel.Error));
    }

    // A rotate may be sent with no body, so its body is optional in the API's document; an issue's body is required.
    [Fact]
    public async Task The_api_document_marks_the_rotate_body_optional_and_the_issue_body_required()
    {
        using var api = new AdminApi();
        using var client = api.CreateSessionClient(AdminSubject);

        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(operation => operation.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                operation.Value.TryGetProperty("operationId", out _))
            .Select(operation => operation.Value)
            .ToList();

        bool? Required(string operationId) =>
            operations.Single(operation => operation.GetProperty("operationId").GetString() == operationId)
                .GetProperty("requestBody").TryGetProperty("required", out var required) ? required.GetBoolean() : null;
        Assert.NotEqual(true, Required("RotateServiceIdentityToken"));
        Assert.Equal(true, Required("IssueServiceIdentityToken"));
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, string path, string body)
    {
        var antiforgery = await client.GetFromJsonAsync<AntiforgeryToken>("/api/auth/antiforgery");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CSRF-TOKEN", antiforgery!.Token);
        return await client.SendAsync(request);
    }

    private static string RotatePath(Guid identityId, Guid tokenId) => $"/api/admin/service-identities/{identityId}/tokens/{tokenId}/rotate";

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
            await SendAsync(client, HttpMethod.Post, $"/api/admin/service-identities/{identityId}/enable", null, withCsrfToken),
            await SendAsync(client, HttpMethod.Get, $"/api/admin/service-identities/{identityId}/tokens", null, withCsrfToken),
            await SendAsync(client, HttpMethod.Delete, $"/api/admin/service-identities/{identityId}/tokens/{Guid.NewGuid()}", null, withCsrfToken),
            await SendAsync(client, HttpMethod.Post, $"/api/admin/service-identities/{identityId}/tokens/{Guid.NewGuid()}/rotate", new { }, withCsrfToken)
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

    private static PersonalAccessToken Token(
        AppUser owner,
        DateTimeOffset? lastUsed = null,
        DateTimeOffset? revoked = null,
        DateTimeOffset? expires = null,
        string name = "token",
        string scope = PersonalAccessTokenScopes.Full,
        DateTimeOffset? created = null) => new()
    {
        AppUserId = owner.Id,
        Name = name,
        Scope = scope,
        TokenPrefix = "prefix",
        TokenHash = Guid.NewGuid().ToString("N"),
        EncryptedSecret = $"v1.secret-{Guid.NewGuid():N}",
        CreatedAtUtc = created ?? DateTimeOffset.UtcNow.AddDays(-2),
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

        /// <summary>Every log entry the application writes.</summary>
        public CapturedLogs Logs { get; } = new();

        public AdminApi(string? adminSubjects = AdminSubject, bool personalAccessTokensEnabled = true, TimeProvider? clock = null)
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
                builder.ConfigureLogging(logging => logging.AddProvider(Logs));
                builder.ConfigureTestServices(services =>
                {
                    if (clock is not null)
                    {
                        services.AddSingleton<TimeProvider>(clock);
                    }

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
        public async Task<ProvisionedServiceIdentity> ProvisionAsync(string name, bool rotate = false)
        {
            using var scope = factory.Services.CreateScope();
            return await ServiceIdentityProvisioner.ProvisionAsync(
                scope.ServiceProvider.GetRequiredService<KanbanDbContext>(),
                scope.ServiceProvider.GetRequiredService<IPersonalAccessTokenService>(),
                name,
                rotate,
                scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow(),
                CancellationToken.None);
        }

        /// <summary>Stores a full-scope token for the user with the application's token service, naming no admin.</summary>
        public async Task<CreatedPersonalAccessToken> CreateTokenAsync(Guid userId, string name)
        {
            using var scope = factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<KanbanDbContext>();
            var user = await dbContext.AppUsers.SingleAsync(candidate => candidate.Id == userId);
            return await scope.ServiceProvider.GetRequiredService<IPersonalAccessTokenService>()
                .CreateAsync(user, name, null, PersonalAccessTokenScopes.Full, CancellationToken.None);
        }

        public HttpClient CreateClient(WebApplicationFactoryClientOptions options) => factory.CreateClient(options);

        /// <summary>The MCP server's client: the internal secret, and a token's identity only when one is forwarded.</summary>
        public HttpClient CreateMcpClient()
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Add(KanbanInternalAuth.SecretHeaderName, ProgramDependencyWiringTests.ApiApplication.InternalSecret);
            return client;
        }

        /// <summary>Makes the application's next SaveChanges throw, once.</summary>
        public bool FailNextSave
        {
            set => inner.FailNextSave = value;
        }

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

    internal sealed class CapturedLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Category, string Message)> entries = new();

        public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => entries.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs logs, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.entries.Enqueue((logLevel, category, $"{formatter(state, exception)} {exception}"));
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
