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
        {
            var identities = await dbContext.AppUsers
                .AsNoTracking()
                .Where(user => user.Issuer == ServiceIdentityProvisioner.Issuer)
                .ToListAsync(cancellationToken);
            var tokens = await TokensOfServiceIdentities(dbContext)
                .Select(token => new { token.AppUserId, token.ExpiresAtUtc, token.RevokedAtUtc, token.LastUsedAtUtc })
                .ToListAsync(cancellationToken);
            var tokensByIdentity = tokens.ToLookup(token => token.AppUserId);
            var now = DateTimeOffset.UtcNow;

            return Results.Ok(identities
                .Select(user => new ServiceIdentitySummaryDto(
                    user.Id,
                    user.DisplayName ?? user.Subject,
                    user.CreatedAtUtc,
                    tokensByIdentity[user.Id].Count(token =>
                        token.RevokedAtUtc is null && (token.ExpiresAtUtc is null || token.ExpiresAtUtc > now)),
                    tokensByIdentity[user.Id].Max(token => token.LastUsedAtUtc)))
                .OrderBy(identity => identity.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
        })
        .WithName("ListServiceIdentities");

        group.MapPost("/service-identities/{identityId:guid}/tokens", async (
            Guid identityId,
            CreatePersonalAccessTokenRequest request,
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

            // Only a service identity: this page never mints a token for a person.
            var identity = await dbContext.AppUsers
                .FirstOrDefaultAsync(user => user.Id == identityId && user.Issuer == ServiceIdentityProvisioner.Issuer, cancellationToken);
            if (identity is null)
            {
                return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Service identity {identityId} was not found.");
            }

            var created = await personalAccessTokenService.CreateAsync(
                identity,
                request.Name,
                request.ExpiresAtUtc,
                KanbanSettingsEndpointRouteBuilderExtensions.NormalizeScope(request.Scope),
                cancellationToken);

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

    private static IResult Refusal(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
