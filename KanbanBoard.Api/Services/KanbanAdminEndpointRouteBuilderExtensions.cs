using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
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

        group.MapGet("/service-identities", async (KanbanDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await SummariesAsync(dbContext, null, cancellationToken)))
        .WithName("ListServiceIdentities");

        group.MapPost("/service-identities", async (
            CreateServiceIdentityRequest request,
            KanbanDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            // The name rules of the create-service-identity command.
            var name = ServiceIdentityProvisioner.NormalizeName(request.Name);
            if (name is null)
            {
                return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, ServiceIdentityProvisioner.InvalidNameMessage);
            }

            var created = await ServiceIdentityProvisioner.CreateAsync(dbContext, name, cancellationToken);
            if (created is null)
            {
                return Refusal(StatusCodes.Status409Conflict, KanbanAdminErrorCodes.IdentityExists,
                    $"A service identity named '{name}' already exists.");
            }

            return Results.Json((await SummariesAsync(dbContext, created.Id, cancellationToken)).Single(), statusCode: StatusCodes.Status201Created);
        })
        .WithName("CreateServiceIdentity");

        group.MapPost("/service-identities/{identityId:guid}/disable", async (
            Guid identityId,
            KanbanDbContext dbContext,
            CancellationToken cancellationToken) =>
            await ServiceIdentityProvisioner.DisableAsync(dbContext, identityId, cancellationToken)
                ? Results.Ok((await SummariesAsync(dbContext, identityId, cancellationToken)).Single())
                : IdentityNotFound(identityId))
        .WithName("DisableServiceIdentity");

        group.MapPost("/service-identities/{identityId:guid}/enable", async (
            Guid identityId,
            KanbanDbContext dbContext,
            CancellationToken cancellationToken) =>
            await ServiceIdentityProvisioner.EnableAsync(dbContext, identityId, cancellationToken)
                ? Results.Ok((await SummariesAsync(dbContext, identityId, cancellationToken)).Single())
                : IdentityNotFound(identityId))
        .WithName("EnableServiceIdentity");

        group.MapPost("/service-identities/{identityId:guid}/tokens", async (
            Guid identityId,
            CreatePersonalAccessTokenRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            IPersonalAccessTokenService personalAccessTokenService,
            IOptions<PersonalAccessTokenOptions> personalAccessTokenOptions,
            CancellationToken cancellationToken) =>
        {
            if (!personalAccessTokenOptions.Value.Enabled)
            {
                return Refusal(StatusCodes.Status503ServiceUnavailable, KanbanAdminErrorCodes.TokensDisabled,
                    "Personal access tokens are disabled on this server.");
            }

            var validationError = KanbanSettingsEndpointRouteBuilderExtensions.ValidateCreatePersonalAccessTokenRequest(request);
            if (validationError is not null)
            {
                return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, validationError);
            }

            // The identity's row is held until the token is stored, so a disable of it waits for this issue (and then
            // revokes the token) or this issue sees the disable and refuses.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            // Only a service identity: this page never mints a token for a person.
            if (await ServiceIdentityProvisioner.LockAsync(dbContext, identityId, cancellationToken) == 0)
            {
                return IdentityNotFound(identityId);
            }

            var identity = await dbContext.AppUsers.FirstAsync(user => user.Id == identityId, cancellationToken);
            if (identity.DisabledAtUtc is not null)
            {
                return Refusal(StatusCodes.Status409Conflict, KanbanAdminErrorCodes.IdentityDisabled,
                    ServiceIdentityProvisioner.DisabledMessage(identity.DisplayName ?? identity.Subject));
            }

            var created = await personalAccessTokenService.CreateAsync(
                identity,
                request.Name,
                request.ExpiresAtUtc,
                KanbanSettingsEndpointRouteBuilderExtensions.NormalizeScope(request.Scope),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // The plaintext is shown once and must not be kept by any cache.
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                new CreatedPersonalAccessTokenDto(
                    KanbanSettingsEndpointRouteBuilderExtensions.ToPersonalAccessTokenMetadataDto(created.Token),
                    created.PlaintextToken),
                statusCode: StatusCodes.Status201Created);
        })
        .WithName("IssueServiceIdentityToken");

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
    /// The summary of every service identity, or of the one with <paramref name="identityId"/>, ordered by name.
    /// </summary>
    public static async Task<List<ServiceIdentitySummaryDto>> SummariesAsync(
        KanbanDbContext dbContext,
        Guid? identityId,
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
        var now = DateTimeOffset.UtcNow;

        return identities
            .Select(user => new ServiceIdentitySummaryDto(
                user.Id,
                user.DisplayName ?? user.Subject,
                user.CreatedAtUtc,
                tokensByIdentity[user.Id].Count(token =>
                    token.RevokedAtUtc is null && (token.ExpiresAtUtc is null || token.ExpiresAtUtc > now)),
                tokensByIdentity[user.Id].Max(token => token.LastUsedAtUtc),
                user.DisabledAtUtc))
            .OrderBy(identity => identity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IResult IdentityNotFound(Guid identityId) =>
        Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Service identity {identityId} was not found.");

    private static IResult Refusal(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
