using System.Text.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Api.Services;

/// <summary>The codes of the admin endpoints' refusals, beside the codes they share with the rest of the API.</summary>
public static class KanbanAdminErrorCodes
{
    /// <summary>The caller is not a signed-in browser session of a user on the admin allowlist.</summary>
    public const string AdminRequired = "admin_required";

    /// <summary>Personal access tokens are disabled on this server, so no token can be issued.</summary>
    public const string TokensDisabled = "tokens_disabled";

    /// <summary>A service identity with that name, in any case, already exists; nothing was created.</summary>
    public const string IdentityExists = "identity_exists";

    /// <summary>The service identity is disabled, so no token is issued for it; nothing was written.</summary>
    public const string IdentityDisabled = "identity_disabled";

    /// <summary>The token is revoked (by an earlier rotate, among others), so it is not rotated; nothing was written.</summary>
    public const string TokenRevoked = "token_revoked";
}

public static class KanbanAdminEndpointRouteBuilderExtensions
{
    public const string AdminPath = "/api/admin";
    public const string NotAdminMessage = "Only an administrator can manage service identities.";

    /// <summary>
    /// True for a signed-in browser session whose OIDC subject is on <see cref="KanbanAuthOptions.AdminSubjects"/>.
    /// Never for a request of the MCP server (internal secret, forwarded token) or one that carries an Authorization
    /// header, which the antiforgery check lets through; never while authentication is disabled.
    /// </summary>
    public static bool IsAdmin(HttpContext context)
    {
        var authOptions = context.RequestServices.GetRequiredService<IOptions<KanbanAuthOptions>>().Value;
        // Without authentication no request carries a signed-in user: the MCP server's are the only ones signed in.
        if (KanbanInternalApiExtensions.IsInternalApiRequest(context) ||
            context.Request.Headers.ContainsKey("Authorization"))
        {
            return false;
        }

        return context.RequestServices.GetRequiredService<ICurrentUserService>()
                .TryGetExternalIdentity(context.User, out var identity) &&
            authOptions.IsAdminSubject(identity.Subject);
    }

    /// <summary>
    /// Refuses every request under <see cref="AdminPath"/> that is not an admin's with 403 <c>admin_required</c>, before
    /// its body is read and before the antiforgery check, so a non-admin learns nothing else about the call.
    /// </summary>
    public static IApplicationBuilder UseKanbanAdminGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(AdminPath, StringComparison.OrdinalIgnoreCase) && !IsAdmin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new KanbanErrorDto(KanbanAdminErrorCodes.AdminRequired, NotAdminMessage));
                return;
            }

            await next(context);
        });

    public static IEndpointRouteBuilder MapKanbanAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(AdminPath)
            .WithTags("Admin");

        group.MapGet("/service-identities", async (KanbanDbContext dbContext, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            Results.Ok(await SummariesAsync(dbContext, null, timeProvider.GetUtcNow(), cancellationToken)))
        .WithName("ListServiceIdentities");

        group.MapPost("/service-identities", async (
            HttpContext httpContext,
            KanbanDbContext dbContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var (request, bodyRefusal) = await ReadBodyAsync<CreateServiceIdentityRequest>(httpContext, cancellationToken);
            if (bodyRefusal is not null)
            {
                return bodyRefusal;
            }

            // The name rules of the create-service-identity command.
            var name = ServiceIdentityProvisioner.NormalizeName(request?.Name);
            if (name is null)
            {
                return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, ServiceIdentityProvisioner.InvalidNameMessage);
            }

            var result = await ServiceIdentityProvisioner.CreateAsync(dbContext, name, cancellationToken);
            switch (result.Outcome)
            {
                case ServiceIdentityCreateOutcome.Busy:
                    return Refusal(StatusCodes.Status503ServiceUnavailable, KanbanErrorCodes.Busy,
                        $"Other service identity creates were in progress for {ServiceIdentityProvisioner.CreateLockTimeoutSeconds} s. Nothing was created; try again.");
                case ServiceIdentityCreateOutcome.Exists:
                    // The identity the name clashed with, which may differ from the requested name in case or accents.
                    return Refusal(StatusCodes.Status409Conflict, KanbanAdminErrorCodes.IdentityExists, result.User is { } existing
                        ? $"A service identity named '{existing.DisplayName ?? existing.Subject}' ({existing.Id}) already exists; '{name}' is the same name."
                        : $"A service identity named '{name}' already exists.");
            }

            var created = result.User!;
            return Results.Json((await SummariesAsync(dbContext, created.Id, timeProvider.GetUtcNow(), cancellationToken)).Single(), statusCode: StatusCodes.Status201Created);
        })
        .Accepts<CreateServiceIdentityRequest>("application/json")
        .WithName("CreateServiceIdentity");

        group.MapPost("/service-identities/{identityId:guid}/disable", async (
            Guid identityId,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            await ServiceIdentityProvisioner.DisableAsync(dbContext, identityId, await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken), cancellationToken)
                ? Results.Ok((await SummariesAsync(dbContext, identityId, timeProvider.GetUtcNow(), cancellationToken)).Single())
                : IdentityNotFound(identityId))
        .WithName("DisableServiceIdentity");

        group.MapPost("/service-identities/{identityId:guid}/enable", async (
            Guid identityId,
            KanbanDbContext dbContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            await ServiceIdentityProvisioner.EnableAsync(dbContext, identityId, cancellationToken)
                ? Results.Ok((await SummariesAsync(dbContext, identityId, timeProvider.GetUtcNow(), cancellationToken)).Single())
                : IdentityNotFound(identityId))
        .WithName("EnableServiceIdentity");

        group.MapPost("/service-identities/{identityId:guid}/tokens", async (
            Guid identityId,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            IPersonalAccessTokenService personalAccessTokenService,
            IOptions<PersonalAccessTokenOptions> personalAccessTokenOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!personalAccessTokenOptions.Value.Enabled)
            {
                return TokensDisabled();
            }

            var (request, bodyRefusal) = await ReadBodyAsync<CreatePersonalAccessTokenRequest>(httpContext, cancellationToken);
            if (bodyRefusal is not null)
            {
                return bodyRefusal;
            }

            var now = timeProvider.GetUtcNow();
            var validationError = request is null
                ? "Field 'name' is required."
                : KanbanSettingsEndpointRouteBuilderExtensions.ValidateCreatePersonalAccessTokenRequest(request, now);
            if (validationError is not null)
            {
                return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, validationError);
            }

            var result = await ServiceIdentityProvisioner.IssueTokenAsync(
                dbContext,
                personalAccessTokenService,
                identityId,
                request!.Name,
                request.ExpiresAtUtc,
                KanbanSettingsEndpointRouteBuilderExtensions.NormalizeScope(request.Scope),
                await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken),
                cancellationToken);
            if (result.Outcome != ServiceIdentityTokenOutcome.Issued)
            {
                return TokenRefusal(result, identityId, null);
            }

            // The plaintext is shown once and must not be kept by any cache.
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                new CreatedPersonalAccessTokenDto(
                    KanbanSettingsEndpointRouteBuilderExtensions.ToPersonalAccessTokenMetadataDto(result.Token!.Token, now),
                    result.Token.PlaintextToken),
                statusCode: StatusCodes.Status201Created);
        })
        .Accepts<CreatePersonalAccessTokenRequest>("application/json")
        .WithName("IssueServiceIdentityToken");

        group.MapGet("/service-identities/{identityId:guid}/tokens", async (
            Guid identityId,
            KanbanDbContext dbContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!await dbContext.AppUsers.AnyAsync(user => user.Id == identityId && user.Issuer == ServiceIdentityProvisioner.Issuer, cancellationToken))
            {
                return IdentityNotFound(identityId);
            }

            var tokens = await dbContext.PersonalAccessTokens
                .AsNoTracking()
                .Where(token => token.AppUserId == identityId)
                .ToListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            return Results.Ok(tokens
                .OrderByDescending(token => token.CreatedAtUtc)
                .ThenBy(token => token.Name)
                .Select(token => new ServiceIdentityTokenDto(
                    token.Id,
                    token.Name,
                    token.Scope,
                    token.TokenPrefix,
                    token.CreatedAtUtc,
                    token.ExpiresAtUtc,
                    token.LastUsedAtUtc,
                    token.RevokedAtUtc,
                    // The rule of the Settings page: revoked before expired before active.
                    token.RevokedAtUtc is not null ? PersonalAccessTokenStates.Revoked
                        : token.ExpiresAtUtc <= now ? PersonalAccessTokenStates.Expired
                        : PersonalAccessTokenStates.Active,
                    AdminOf(token.IssuedByUserId, token.IssuedByName),
                    AdminOf(token.RevokedByUserId, token.RevokedByName)))
                .ToList());
        })
        .WithName("ListServiceIdentityTokens");

        // As the Settings page revokes a token: DELETE, 204, and revoking a revoked token succeeds and changes nothing.
        group.MapDelete("/service-identities/{identityId:guid}/tokens/{tokenId:guid}", async (
            Guid identityId,
            Guid tokenId,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            CancellationToken cancellationToken) =>
            await ServiceIdentityProvisioner.RevokeTokenAsync(
                dbContext, identityId, tokenId, await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken), cancellationToken)
                ? Results.NoContent()
                : TokenNotFound(identityId, tokenId))
        .WithName("RevokeServiceIdentityToken");

        group.MapPost("/service-identities/{identityId:guid}/tokens/{tokenId:guid}/rotate", async (
            Guid identityId,
            Guid tokenId,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            IPersonalAccessTokenService personalAccessTokenService,
            IOptions<PersonalAccessTokenOptions> personalAccessTokenOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!personalAccessTokenOptions.Value.Enabled)
            {
                return TokensDisabled();
            }

            // No body, or a body without an expiry: the replacement does not expire.
            var (request, bodyRefusal) = await ReadBodyAsync<RotatePersonalAccessTokenRequest>(httpContext, cancellationToken);
            if (bodyRefusal is not null)
            {
                return bodyRefusal;
            }

            var now = timeProvider.GetUtcNow();
            var expiresAtUtc = request?.ExpiresAtUtc;
            if (expiresAtUtc <= now)
            {
                return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, "Field 'expiresAtUtc' must be in the future.");
            }

            var result = await ServiceIdentityProvisioner.RotateTokenAsync(
                dbContext, personalAccessTokenService, identityId, tokenId, expiresAtUtc,
                await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken), cancellationToken);
            if (result.Outcome != ServiceIdentityTokenOutcome.Issued)
            {
                return TokenRefusal(result, identityId, tokenId);
            }

            // The plaintext is shown once and must not be kept by any cache.
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                new RotatedPersonalAccessTokenDto(
                    KanbanSettingsEndpointRouteBuilderExtensions.ToPersonalAccessTokenMetadataDto(result.Token!.Token, now),
                    result.Token.PlaintextToken,
                    tokenId),
                statusCode: StatusCodes.Status201Created);
        })
        .Accepts<RotatePersonalAccessTokenRequest>(isOptional: true, "application/json")
        .WithName("RotateServiceIdentityToken");

        return endpoints;
    }

    /// <summary>
    /// The tokens of every service identity, by a join: MySql.EntityFrameworkCore cannot translate a list of ids
    /// given to <c>Contains</c>.
    /// </summary>
    public static IQueryable<PersonalAccessToken> TokensOfServiceIdentities(KanbanDbContext dbContext) =>
        dbContext.PersonalAccessTokens
            .AsNoTracking()
            .Where(token => token.AppUser!.Issuer == ServiceIdentityProvisioner.Issuer);

    /// <summary>
    /// The summary of every service identity, or of the one with <paramref name="identityId"/>, ordered by name; a
    /// token is active when it is neither revoked nor expired at <paramref name="now"/>.
    /// </summary>
    public static async Task<List<ServiceIdentitySummaryDto>> SummariesAsync(
        KanbanDbContext dbContext,
        Guid? identityId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var identityQuery = dbContext.AppUsers
            .AsNoTracking()
            .Where(user => user.Issuer == ServiceIdentityProvisioner.Issuer);
        var tokenQuery = TokensOfServiceIdentities(dbContext);
        if (identityId is { } id)
        {
            identityQuery = identityQuery.Where(user => user.Id == id);
            tokenQuery = tokenQuery.Where(token => token.AppUserId == id);
        }

        var identities = await identityQuery.ToListAsync(cancellationToken);
        var tokens = await tokenQuery
            .Select(token => new { token.AppUserId, token.ExpiresAtUtc, token.RevokedAtUtc, token.LastUsedAtUtc })
            .ToListAsync(cancellationToken);
        var tokensByIdentity = tokens.ToLookup(token => token.AppUserId);

        return identities
            .Select(user => new ServiceIdentitySummaryDto(
                user.Id,
                user.DisplayName ?? user.Subject,
                user.CreatedAtUtc,
                // None of a disabled identity's tokens is accepted, whether or not it was revoked.
                user.DisabledAtUtc is not null
                    ? 0
                    : tokensByIdentity[user.Id].Count(token =>
                        token.RevokedAtUtc is null && (token.ExpiresAtUtc is null || token.ExpiresAtUtc > now)),
                tokensByIdentity[user.Id].Max(token => token.LastUsedAtUtc),
                user.DisabledAtUtc))
            .OrderBy(identity => identity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PersonalAccessTokenAdminDto? AdminOf(Guid? id, string? name) =>
        id is null && name is null ? null : new PersonalAccessTokenAdminDto(id, name);

    /// <summary>
    /// The JSON body of an admin request, read here rather than bound: a body that is not JSON of the request's shape
    /// is refused with 400 <c>invalid_argument</c> and a message, where binding answers a bare 400. An empty body, or
    /// JSON <c>null</c>, reads as null.
    /// </summary>
    private static async Task<(T? Body, IResult? Refusal)> ReadBodyAsync<T>(HttpContext httpContext, CancellationToken cancellationToken)
        where T : class
    {
        using var reader = new StreamReader(httpContext.Request.Body);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        try
        {
            var options = httpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            return (JsonSerializer.Deserialize<T>(text, options), null);
        }
        catch (JsonException exception)
        {
            return (null, Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument,
                $"The request body is not valid JSON of the expected shape: {exception.Message}"));
        }
    }

    private static IResult TokensDisabled() =>
        Refusal(StatusCodes.Status503ServiceUnavailable, KanbanAdminErrorCodes.TokensDisabled,
            "Personal access tokens are disabled on this server.");

    private static IResult TokenRefusal(ServiceIdentityTokenResult result, Guid identityId, Guid? tokenId) => result.Outcome switch
    {
        ServiceIdentityTokenOutcome.IdentityDisabled => Refusal(StatusCodes.Status409Conflict, KanbanAdminErrorCodes.IdentityDisabled,
            ServiceIdentityProvisioner.DisabledMessage(result.Identity!.DisplayName ?? result.Identity.Subject)),
        ServiceIdentityTokenOutcome.TokenNotFound => TokenNotFound(identityId, tokenId!.Value),
        ServiceIdentityTokenOutcome.TokenRevoked => Refusal(StatusCodes.Status409Conflict, KanbanAdminErrorCodes.TokenRevoked,
            $"Token {tokenId} is revoked, so it is not rotated; nothing was written. Issue a new token instead."),
        _ => IdentityNotFound(identityId)
    };

    private static IResult TokenNotFound(Guid identityId, Guid tokenId) =>
        Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Service identity {identityId} has no token {tokenId}.");

    private static IResult IdentityNotFound(Guid identityId) =>
        Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Service identity {identityId} was not found.");

    private static IResult Refusal(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
