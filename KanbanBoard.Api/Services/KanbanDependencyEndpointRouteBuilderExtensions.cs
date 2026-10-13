using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class KanbanDependencyEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapKanbanDependencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/items/{itemId:guid}/dependencies", async (
            Guid itemId,
            AddWorkItemDependencyRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            await HandleAsync(itemId, request.BlockedByItemId, dbContext, changeNotifier,
                WorkItemDependencies.AddAsync(
                    dbContext, itemId, request.BlockedByItemId,
                    await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken), timeProvider.GetUtcNow(), cancellationToken),
                cancellationToken))
            .WithName("AddWorkItemDependency");

        endpoints.MapDelete("/api/items/{itemId:guid}/dependencies/{blockedByItemId:guid}", async (
            Guid itemId,
            Guid blockedByItemId,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            await HandleAsync(itemId, blockedByItemId, dbContext, changeNotifier,
                WorkItemDependencies.RemoveAsync(
                    dbContext, itemId, blockedByItemId,
                    await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken), timeProvider.GetUtcNow(), cancellationToken),
                cancellationToken))
            .WithName("RemoveWorkItemDependency");

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        Guid itemId,
        Guid blockedByItemId,
        KanbanDbContext dbContext,
        BoardChangeNotifier changeNotifier,
        Task<WorkItemDependencyOutcome> write,
        CancellationToken cancellationToken)
    {
        var outcome = await write;
        if (outcome == WorkItemDependencyOutcome.Cycle)
        {
            return Error(StatusCodes.Status409Conflict, KanbanErrorCodes.Cycle,
                itemId == blockedByItemId
                    ? $"Work item {itemId} cannot be blocked by itself. Nothing was changed."
                    : $"Work item {blockedByItemId} is already blocked, directly or through other items, by work item {itemId}, so the dependency would form a cycle. Nothing was changed.");
        }

        if (outcome == WorkItemDependencyOutcome.Busy)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, KanbanErrorCodes.Busy,
                $"Other dependency changes were in progress for {WorkItemDependencies.LockTimeoutSeconds} s. Nothing was changed; try again.");
        }

        var item = outcome == WorkItemDependencyOutcome.NotFound
            ? null
            : await dbContext.WorkItems
                .AsNoTracking()
                .Include(workItem => workItem.Epic)
                .Include(workItem => workItem.Comments)
                .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound,
                $"Work item {itemId} or {blockedByItemId} was not found.");
        }

        changeNotifier.Publish();
        return Results.Ok(WorkItemDtoMapper.ToDto(
            item,
            DateTimeOffset.UtcNow,
            await WorkItemDependencies.LoadAsync(dbContext, itemId, cancellationToken),
            await WorkItemLinks.LoadAsync(dbContext, itemId, cancellationToken)));
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
