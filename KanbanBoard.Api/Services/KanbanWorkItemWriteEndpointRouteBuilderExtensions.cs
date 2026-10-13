using System.Text.Json;
using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Api.Services;

public static class KanbanWorkItemWriteEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapKanbanWorkItemWriteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The update of the web UI, which sends every field. It is the same merge as the PATCH below, except that a
        // field a work item does not have is ignored, as it always was.
        endpoints.MapPut("/api/items/{itemId:guid}", (
            Guid itemId,
            JsonElement payload,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            UpdateAsync(itemId, payload, strict: false, dbContext, changeNotifier, timeProvider, cancellationToken))
        .WithName("UpdateWorkItem");

        // The partial update: a field that is present is written, null clears it, an absent field is left alone.
        // With expectedVersion the update is refused 409 conflict unless the item still has that version.
        endpoints.MapPatch("/api/items/{itemId:guid}", (
            Guid itemId,
            JsonElement payload,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            UpdateAsync(itemId, payload, strict: true, dbContext, changeNotifier, timeProvider, cancellationToken))
        .WithName("PatchWorkItem");

        endpoints.MapPost("/api/items/{itemId:guid}/move", async (
            Guid itemId,
            MoveWorkItemRequest request,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            await AnswerAsync(
                itemId,
                await WorkItemWrites.MoveAsync(dbContext, itemId, request.Status, request.Order, request.ExpectedVersion, timeProvider.GetUtcNow(), cancellationToken),
                dbContext, changeNotifier, timeProvider, cancellationToken))
        .WithName("MoveWorkItem");

        return endpoints;
    }

    private static async Task<IResult> UpdateAsync(
        Guid itemId,
        JsonElement payload,
        bool strict,
        KanbanDbContext dbContext,
        BoardChangeNotifier changeNotifier,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!UpdateWorkItemPatch.TryParse(payload, strict, out var patch, out var parseError))
        {
            return Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, parseError!);
        }

        return await AnswerAsync(
            itemId,
            await WorkItemWrites.UpdateAsync(dbContext, itemId, patch!, timeProvider.GetUtcNow(), cancellationToken),
            dbContext, changeNotifier, timeProvider, cancellationToken);
    }

    private static async Task<IResult> AnswerAsync(
        Guid itemId,
        WorkItemWriteResult result,
        KanbanDbContext dbContext,
        BoardChangeNotifier changeNotifier,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case WorkItemWriteOutcome.NotFound:
                return Error(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Work item {itemId} was not found.");
            case WorkItemWriteOutcome.VersionConflict:
                return Error(StatusCodes.Status409Conflict, KanbanErrorCodes.Conflict, result.Message!);
            case WorkItemWriteOutcome.Invalid:
                return Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, result.Message!);
        }

        changeNotifier.Publish();
        return Results.Ok(WorkItemDtoMapper.ToDto(
            result.Item!,
            timeProvider.GetUtcNow(),
            await WorkItemDependencies.LoadAsync(dbContext, itemId, cancellationToken),
            await WorkItemLinks.LoadAsync(dbContext, itemId, cancellationToken)));
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
