using System.Text.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;
using Microsoft.Extensions.Options;

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
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            IOptions<AppOptions> appOptions,
            CancellationToken cancellationToken) =>
            UpdateAsync(itemId, payload, strict: false, httpContext, dbContext, changeNotifier, timeProvider, appOptions, cancellationToken))
        .WithName("UpdateWorkItem");

        // The partial update: a field that is present is written, null clears it, an absent field is left alone.
        // With expectedVersion the update is refused 409 conflict unless the item still has that version. A body
        // that names no field is refused: it would advance the version and fail another caller's guarded write.
        endpoints.MapPatch("/api/items/{itemId:guid}", (
            Guid itemId,
            JsonElement payload,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            IOptions<AppOptions> appOptions,
            CancellationToken cancellationToken) =>
            UpdateAsync(itemId, payload, strict: true, httpContext, dbContext, changeNotifier, timeProvider, appOptions, cancellationToken))
        .WithName("PatchWorkItem");

        endpoints.MapPost("/api/items/{itemId:guid}/move", async (
            Guid itemId,
            MoveWorkItemRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            IOptions<AppOptions> appOptions,
            CancellationToken cancellationToken) =>
            await AnswerAsync(
                itemId,
                await WorkItemWrites.MoveAsync(
                    dbContext, itemId, request.Status, request.Order, request.ExpectedVersion,
                    await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken), timeProvider.GetUtcNow(), cancellationToken),
                dbContext, changeNotifier, timeProvider, appOptions, cancellationToken))
        .WithName("MoveWorkItem");

        return endpoints;
    }

    private static async Task<IResult> UpdateAsync(
        Guid itemId,
        JsonElement payload,
        bool strict,
        HttpContext httpContext,
        KanbanDbContext dbContext,
        BoardChangeNotifier changeNotifier,
        TimeProvider timeProvider,
        IOptions<AppOptions> appOptions,
        CancellationToken cancellationToken)
    {
        if (!UpdateWorkItemPatch.TryParse(payload, strict, out var patch, out var parseError))
        {
            return Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, parseError!);
        }

        if (strict && !patch!.HasAnyField)
        {
            return Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, "The update names no field to change. Nothing was changed.");
        }

        return await AnswerAsync(
            itemId,
            await WorkItemWrites.UpdateAsync(
                dbContext, itemId, patch!, await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken), timeProvider.GetUtcNow(), cancellationToken),
            dbContext, changeNotifier, timeProvider, appOptions, cancellationToken);
    }

    private static async Task<IResult> AnswerAsync(
        Guid itemId,
        WorkItemWriteResult result,
        KanbanDbContext dbContext,
        BoardChangeNotifier changeNotifier,
        TimeProvider timeProvider,
        IOptions<AppOptions> appOptions,
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
            appOptions.Value,
            await WorkItemDependencies.LoadAsync(dbContext, itemId, cancellationToken),
            await WorkItemLinks.LoadAsync(dbContext, itemId, cancellationToken)));
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
