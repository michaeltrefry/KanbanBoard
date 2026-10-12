using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class KanbanLinkEndpointRouteBuilderExtensions
{
    private static readonly string KindMessage =
        $"must be one of: {string.Join(", ", WorkItemLinkKinds.All)}.";

    public static IEndpointRouteBuilder MapKanbanLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Error messages name the arguments as the MCP tools spell them.
        endpoints.MapPost("/api/items/{itemId:guid}/links", async (
            Guid itemId,
            AddWorkItemLinkRequest request,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!WorkItemLinks.TryNormalizeKind(request.Kind, out var kind))
            {
                return InvalidArgument($"Argument 'kind' {KindMessage}");
            }

            if (!WorkItemLinks.TryNormalizeUrl(request.Url, out var url))
            {
                return InvalidArgument(
                    $"Argument 'url' must be an absolute http or https URL of at most {WorkItemLinkLimits.MaxUrlLength} characters " +
                    $"and at most {WorkItemLinkLimits.MaxEscapedUrlLength} characters when percent-encoded, " +
                    "without whitespace, control characters or credentials.");
            }

            return await RespondAsync(
                itemId,
                await WorkItemLinks.AddAsync(dbContext, itemId, kind, url, timeProvider.GetUtcNow(), cancellationToken),
                dbContext, changeNotifier, timeProvider, cancellationToken);
        })
        .WithName("AddWorkItemLink");

        // Addresses the link by linkId, or by kind and url.
        endpoints.MapDelete("/api/items/{itemId:guid}/links", async (
            Guid itemId,
            Guid? linkId,
            string? kind,
            string? url,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            string? normalizedKind = null;
            var normalizedUrl = url?.Trim();
            if (linkId is null ? kind is null || string.IsNullOrEmpty(normalizedUrl) : kind is not null || url is not null)
            {
                return InvalidArgument("Pass either 'link_id', or both 'kind' and 'url'.");
            }

            if (linkId is null && !WorkItemLinks.TryNormalizeKind(kind, out normalizedKind))
            {
                return InvalidArgument($"Argument 'kind' {KindMessage}");
            }

            if (linkId is null && !WorkItemLinkLimits.FitsLookup(normalizedUrl!))
            {
                return InvalidArgument(WorkItemLinkLimits.LookupUrlTooLongMessage);
            }

            return await RespondAsync(
                itemId,
                await WorkItemLinks.RemoveAsync(dbContext, itemId, linkId, normalizedKind, linkId is null ? normalizedUrl : null, timeProvider.GetUtcNow(), cancellationToken),
                dbContext, changeNotifier, timeProvider, cancellationToken);
        })
        .WithName("RemoveWorkItemLink");

        return endpoints;
    }

    private static async Task<IResult> RespondAsync(
        Guid itemId,
        WorkItemLinkOutcome outcome,
        KanbanDbContext dbContext,
        BoardChangeNotifier changeNotifier,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var item = outcome == WorkItemLinkOutcome.NotFound
            ? null
            : await dbContext.WorkItems
                .AsNoTracking()
                .Include(workItem => workItem.Epic)
                .Include(workItem => workItem.Comments)
                .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Results.Json(
                new KanbanErrorDto(KanbanErrorCodes.NotFound, $"Work item {itemId} was not found."),
                statusCode: StatusCodes.Status404NotFound);
        }

        if (outcome == WorkItemLinkOutcome.Succeeded)
        {
            changeNotifier.Publish();
        }

        return Results.Ok(WorkItemDtoMapper.ToDto(
            item,
            timeProvider.GetUtcNow(),
            await WorkItemDependencies.LoadAsync(dbContext, itemId, cancellationToken),
            await WorkItemLinks.LoadAsync(dbContext, itemId, cancellationToken)));
    }

    private static IResult InvalidArgument(string message) =>
        Results.Json(new KanbanErrorDto(KanbanErrorCodes.InvalidArgument, message), statusCode: StatusCodes.Status400BadRequest);
}
