using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class KanbanWorkItemReadEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapKanbanWorkItemReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/items", async (Guid? projectId, Guid? epicId, WorkItemType? type, WorkItemStatus? status, bool? includeArchivedEpics, bool? unblockedOnly, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
        {
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

            var items = await query
                .Include(item => item.Epic)
                .Include(item => item.Comments)
                .OrderBy(item => item.Status)
                .ThenBy(item => item.Order)
                .ToListAsync(cancellationToken);
            var dependencies = await WorkItemDependencies.LoadAsync(dbContext, query, cancellationToken);
            var now = DateTimeOffset.UtcNow;

            return Results.Ok(items
                .OrderBy(item => item.Status)
                .ThenBy(item => item.Order)
                .ThenBy(item => item.CreatedAtUtc)
                .Select(item => WorkItemDtoMapper.ToDto(item, now, dependencies))
                .ToList());
        })
        .WithName("ListWorkItems");

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
                    await WorkItemDependencies.LoadAsync(dbContext, itemId, cancellationToken)));
        })
        .WithName("GetWorkItem");

        return endpoints;
    }
}
