using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class KanbanWorkItemReadEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapKanbanWorkItemReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/items", async (Guid? projectId, Guid? epicId, WorkItemType? type, WorkItemStatus? status, bool? includeArchivedEpics, bool? unblockedOnly, string? label, string? updatedSince, string? claimState, int? limit, string? cursor, bool? includeComments, KanbanDbContext dbContext, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            // Error messages name the arguments as the MCP tool spells them.
            var normalizedLabel = label?.Trim();
            if (normalizedLabel is not null && (normalizedLabel.Length == 0 || normalizedLabel.Contains(',')))
            {
                return InvalidArgument("Argument 'label' must be one label: not empty and without a comma.");
            }

            DateTimeOffset since = default;
            if (updatedSince is not null && !WorkItemListing.TryParseTimestamp(updatedSince, out since))
            {
                return InvalidArgument("Argument 'updated_since' must be an ISO 8601 timestamp such as 2026-10-12T12:00:00Z.");
            }

            WorkItemClaimState claim = default;
            if (claimState is not null && !WorkItemListing.TryParseClaimState(claimState, out claim))
            {
                return InvalidArgument("Argument 'claim_state' must be one of: unclaimed, claimed, expired.");
            }

            if (limit is < 1 or > WorkItemListing.MaxPageSize)
            {
                return InvalidArgument($"Argument 'limit' must be between 1 and {WorkItemListing.MaxPageSize}.");
            }

            WorkItemCursor after = default;
            if (cursor is not null && !WorkItemCursor.TryDecode(cursor, out after))
            {
                return InvalidArgument("Argument 'cursor' is not a cursor returned by this tool. Pass next_cursor from the previous page unchanged.");
            }

            var now = timeProvider.GetUtcNow();
            var includeArchivedEpicItems = includeArchivedEpics ?? false;
            var query = dbContext.WorkItems.AsNoTracking();

            if (projectId is not null)
            {
                query = query.Where(item => item.ProjectId == projectId);
            }

            if (epicId is not null)
            {
                query = query.Where(item => item.EpicId == epicId);
            }

            if (type is not null)
            {
                query = query.Where(item => item.Type == type);
            }

            if (status is not null)
            {
                query = query.Where(item => item.Status == status);
            }
            else
            {
                query = query.Where(item => item.Status != WorkItemStatus.Closed);
            }

            if (!includeArchivedEpicItems)
            {
                query = query.Where(item => item.Epic == null || !item.Epic.IsArchived);
            }

            if (unblockedOnly == true)
            {
                query = query.WhereUnblocked(dbContext);
            }

            if (normalizedLabel is not null)
            {
                query = query.WhereHasLabel(normalizedLabel);
            }

            if (updatedSince is not null)
            {
                query = query.Where(item => item.UpdatedAtUtc >= since);
            }

            if (claimState is not null)
            {
                query = query.WhereClaimState(claim, now);
            }

            // Without comments the items are returned with an empty comments list.
            IQueryable<WorkItem> WithIncludes(IQueryable<WorkItem> source) =>
                includeComments == false
                    ? source.Include(item => item.Epic)
                    : source.Include(item => item.Epic).Include(item => item.Comments);

            if (limit is not null || cursor is not null)
            {
                if (cursor is not null)
                {
                    query = query.WhereAfter(after);
                }

                // One row more than the page, to know whether another page follows.
                var pageSize = limit ?? WorkItemListing.DefaultPageSize;
                var rows = await WithIncludes(query.InPagingOrder().Take(pageSize + 1)).ToListAsync(cancellationToken);
                var page = rows.Take(pageSize).ToList();
                if (page.Count == 0)
                {
                    return Results.Ok(new WorkItemPageDto([], null));
                }

                // The page is a range of the paging order, which bounds the dependency lookup without a list of ids.
                var lastCreatedAtUtc = page[^1].CreatedAtUtc;
                var pageDependencies = await WorkItemDependencies.LoadAsync(
                    dbContext, query.Where(item => item.CreatedAtUtc <= lastCreatedAtUtc), cancellationToken);
                var pageLinks = await WorkItemLinks.LoadAsync(
                    dbContext, query.Where(item => item.CreatedAtUtc <= lastCreatedAtUtc), cancellationToken);

                return Results.Ok(new WorkItemPageDto(
                    page.Select(item => WorkItemDtoMapper.ToDto(item, now, pageDependencies, pageLinks)).ToList(),
                    rows.Count > pageSize ? WorkItemCursor.After(page[^1]).Encode() : null));
            }

            var items = await WithIncludes(query)
                .OrderBy(item => item.Status)
                .ThenBy(item => item.Order)
                .ToListAsync(cancellationToken);
            var dependencies = await WorkItemDependencies.LoadAsync(dbContext, query, cancellationToken);
            var links = await WorkItemLinks.LoadAsync(dbContext, query, cancellationToken);

            return Results.Ok(items
                .OrderBy(item => item.Status)
                .ThenBy(item => item.Order)
                .ThenBy(item => item.CreatedAtUtc)
                .Select(item => WorkItemDtoMapper.ToDto(item, now, dependencies, links))
                .ToList());
        })
        .WithName("ListWorkItems");

        // Every item that carries the URL, whatever its status, project or epic, oldest first.
        endpoints.MapGet("/api/items/by-link", async (string? url, KanbanDbContext dbContext, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var normalizedUrl = url?.Trim();
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                return InvalidArgument("Argument 'url' is required.");
            }

            var query = dbContext.WorkItems.AsNoTracking().WhereHasLink(dbContext, normalizedUrl);
            var items = await query
                .Include(item => item.Epic)
                .Include(item => item.Comments)
                .InPagingOrder()
                .ToListAsync(cancellationToken);
            var dependencies = await WorkItemDependencies.LoadAsync(dbContext, query, cancellationToken);
            var links = await WorkItemLinks.LoadAsync(dbContext, query, cancellationToken);
            var now = timeProvider.GetUtcNow();

            return Results.Ok(items.Select(item => WorkItemDtoMapper.ToDto(item, now, dependencies, links)).ToList());
        })
        .WithName("FindWorkItemsByLink");

        endpoints.MapGet("/api/items/{itemId:guid}", async (Guid itemId, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var item = await dbContext.WorkItems
                .AsNoTracking()
                .Include(workItem => workItem.Epic)
                .Include(workItem => workItem.Comments)
                .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);

            return item is null
                ? Results.NotFound()
                : Results.Ok(WorkItemDtoMapper.ToDto(
                    item,
                    DateTimeOffset.UtcNow,
                    await WorkItemDependencies.LoadAsync(dbContext, itemId, cancellationToken),
                    await WorkItemLinks.LoadAsync(dbContext, itemId, cancellationToken)));
        })
        .WithName("GetWorkItem");

        return endpoints;
    }

    private static IResult InvalidArgument(string message) =>
        Results.Json(new KanbanErrorDto(KanbanErrorCodes.InvalidArgument, message), statusCode: StatusCodes.Status400BadRequest);
}
