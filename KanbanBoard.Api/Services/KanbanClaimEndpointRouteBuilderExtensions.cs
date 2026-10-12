using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class KanbanClaimEndpointRouteBuilderExtensions
{
    public const string NoClaimantMessage =
        "Claims are made through the MCP server with a personal access token; this request carries no token identity.";

    public static IEndpointRouteBuilder MapKanbanClaimEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/items/{itemId:guid}/claim", (
            Guid itemId,
            ClaimWorkItemRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            HandleAsync(itemId, request.Marker, request.TtlSeconds, httpContext, dbContext, timeProvider,
                (claimant, marker, ttl, now) => WorkItemClaims.ClaimAsync(dbContext, itemId, claimant, marker, ttl!.Value, now, cancellationToken),
                cancellationToken))
            .WithName("ClaimWorkItem");

        endpoints.MapPost("/api/items/{itemId:guid}/claim/renew", (
            Guid itemId,
            ClaimWorkItemRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            HandleAsync(itemId, request.Marker, request.TtlSeconds, httpContext, dbContext, timeProvider,
                (claimant, marker, ttl, now) => WorkItemClaims.RenewAsync(dbContext, itemId, claimant, marker, ttl!.Value, now, cancellationToken),
                cancellationToken))
            .WithName("RenewWorkItemClaim");

        endpoints.MapPost("/api/items/{itemId:guid}/claim/release", (
            Guid itemId,
            ReleaseWorkItemClaimRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            HandleAsync(itemId, request.Marker, null, httpContext, dbContext, timeProvider,
                (claimant, marker, _, now) => WorkItemClaims.ReleaseAsync(dbContext, itemId, claimant, marker, now, cancellationToken),
                cancellationToken))
            .WithName("ReleaseWorkItemClaim");

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        Guid itemId,
        string? requestedMarker,
        int? ttlSeconds,
        HttpContext httpContext,
        KanbanDbContext dbContext,
        TimeProvider timeProvider,
        Func<AppUser, string, TimeSpan?, DateTimeOffset, Task<WorkItemClaimResult>> write,
        CancellationToken cancellationToken)
    {
        // The claimant is whoever owns the forwarded token. No request field can name a different one.
        var claimant = await ResolveClaimantAsync(httpContext, dbContext, cancellationToken);
        if (claimant is null)
        {
            return Error(StatusCodes.Status403Forbidden, KanbanErrorCodes.ScopeDenied, NoClaimantMessage);
        }

        var marker = requestedMarker?.Trim();
        if (string.IsNullOrEmpty(marker) || marker.Length > WorkItemClaimLimits.MaxMarkerLength)
        {
            return Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument,
                $"Field 'marker' is required and must be at most {WorkItemClaimLimits.MaxMarkerLength} characters.");
        }

        if (ttlSeconds is < WorkItemClaimLimits.MinTtlSeconds or > WorkItemClaimLimits.MaxTtlSeconds)
        {
            return Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument,
                $"Field 'ttl_seconds' must be between {WorkItemClaimLimits.MinTtlSeconds} and {WorkItemClaimLimits.MaxTtlSeconds}.");
        }

        var now = timeProvider.GetUtcNow();
        var result = await write(claimant, marker, ttlSeconds is null ? null : TimeSpan.FromSeconds(ttlSeconds.Value), now);
        var current = result.Current;

        return result.Outcome switch
        {
            WorkItemClaimOutcome.Succeeded => Results.Ok(WorkItemDtoMapper.ToDto(current!, now)),
            WorkItemClaimOutcome.NotFound => Error(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound,
                $"Work item {itemId} was not found."),
            WorkItemClaimOutcome.AlreadyClaimed => Error(StatusCodes.Status409Conflict, KanbanErrorCodes.AlreadyClaimed,
                current!.ClaimExpiresAtUtc > now
                    ? $"Work item {itemId} is already claimed by '{current.ClaimantName}' with marker '{current.ClaimMarker}' until {current.ClaimExpiresAtUtc:O}."
                    : $"Work item {itemId} was claimed by another caller at the same moment."),
            _ => Error(StatusCodes.Status409Conflict, KanbanErrorCodes.Conflict,
                $"Work item {itemId} has no live claim held by this caller with marker '{marker}'; it lapsed or was released. Nothing was changed.")
        };
    }

    private static async Task<AppUser?> ResolveClaimantAsync(
        HttpContext httpContext,
        KanbanDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!KanbanInternalApiExtensions.IsInternalApiRequest(httpContext) ||
            !Guid.TryParse(
                httpContext.User.FindFirst(KanbanInternalAuth.PersonalAccessTokenIdHeaderName)?.Value,
                out var tokenId))
        {
            return null;
        }

        return await dbContext.PersonalAccessTokens
            .AsNoTracking()
            .Where(token => token.Id == tokenId && token.RevokedAtUtc == null)
            .Select(token => token.AppUser)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
