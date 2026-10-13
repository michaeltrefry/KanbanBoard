using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class KanbanChangeFeedEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapKanbanChangeFeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The persisted change feed, oldest entry first. Not the stream of GET /api/changes, which only signals
        // that something changed. Error messages name the arguments as the MCP tool spells them.
        //
        // Without a cursor the answer holds no entry and the cursor of the newest one: the place to start
        // following from. With fromStart it starts at the oldest entry instead. The answer always carries a
        // cursor, which is the position after the entries it returned.
        endpoints.MapGet("/api/items/changes", async (
            string? cursor,
            Guid? projectId,
            int? limit,
            bool? fromStart,
            KanbanDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (limit is < 1 or > WorkItemListing.MaxPageSize)
            {
                return InvalidArgument($"Argument 'limit' must be between 1 and {WorkItemListing.MaxPageSize}.");
            }

            if (cursor is not null && fromStart == true)
            {
                return InvalidArgument("Pass either 'cursor' or 'from_start', not both.");
            }

            long after = 0;
            if (cursor is not null && !WorkItemChangeLog.TryDecodeCursor(cursor, out after))
            {
                return InvalidArgument("Argument 'cursor' is not a cursor returned by this tool. Pass next_cursor from the previous call unchanged.");
            }

            if (cursor is null && fromStart != true)
            {
                // The newest entry of the whole feed, whatever the project: a cursor is a position in the feed.
                var head = await dbContext.WorkItemChanges.MaxAsync(change => (long?)change.Sequence, cancellationToken) ?? 0;
                return Results.Ok(new WorkItemChangePageDto([], WorkItemChangeLog.EncodeCursor(head)));
            }

            var query = dbContext.WorkItemChanges.AsNoTracking().Where(change => change.Sequence > after);
            if (projectId is not null)
            {
                query = query.Where(change => change.ProjectId == projectId);
            }

            var page = await query
                .OrderBy(change => change.Sequence)
                .Take(limit ?? WorkItemListing.DefaultPageSize)
                .ToListAsync(cancellationToken);

            return Results.Ok(new WorkItemChangePageDto(
                page.Select(WorkItemChangeLog.ToDto).ToList(),
                WorkItemChangeLog.EncodeCursor(page.Count == 0 ? after : page[^1].Sequence)));
        })
        .WithName("ListWorkItemChanges");

        return endpoints;
    }

    private static IResult InvalidArgument(string message) =>
        Results.Json(new KanbanErrorDto(KanbanErrorCodes.InvalidArgument, message), statusCode: StatusCodes.Status400BadRequest);
}
