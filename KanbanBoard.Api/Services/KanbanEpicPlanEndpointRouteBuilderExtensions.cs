using System.Text.Json;
using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Api.Services;

public static class KanbanEpicPlanEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapKanbanEpicPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Creates an epic, its documents, its work items and their dependencies in one transaction: all of them or
        // none. The idempotency key is required: a repeat of it returns the ids of the first import with
        // "created": false and status 200, and creates nothing. Error messages name the arguments as the MCP tool
        // spells them.
        endpoints.MapPost("/api/projects/{projectId:guid}/epic-plans", async (
            Guid projectId,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // The body is read here rather than bound, so that a plan that is too large or not a plan is refused
            // with a code like every other refusal.
            var body = await ReadBodyAsync(httpContext.Request, cancellationToken);
            if (body is null)
            {
                return InvalidArgument($"The plan is larger than {EpicPlanLimits.MaxRequestBytes} bytes of JSON. Nothing was created; import a smaller plan.");
            }

            ImportEpicPlanRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ImportEpicPlanRequest>(body, JsonOptions);
            }
            catch (JsonException exception)
            {
                return InvalidArgument($"The plan could not be read: {exception.Message}");
            }

            if (request is null)
            {
                return InvalidArgument("The plan could not be read: the request body is null.");
            }

            var now = timeProvider.GetUtcNow();
            if (request.IdempotencyKey is null)
            {
                return InvalidArgument("Argument 'idempotency_key' is required: it is what makes an import safe to repeat.");
            }

            var read = await EpicPlans.ReadAsync(projectId, request, now);
            if (read.Plan is null)
            {
                return Error(StatusCodes.Status400BadRequest, read.Code!, read.Message!);
            }

            var (keyHash, keyRefusal) = await IdempotencyKeys.HashForRequestAsync(httpContext, dbContext, request.IdempotencyKey, now, cancellationToken);
            if (keyRefusal is not null)
            {
                return keyRefusal;
            }

            var actor = await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken);
            var result = await EpicPlans.ImportAsync(dbContext, read.Plan, keyHash!, actor, now, cancellationToken);
            if (result.Outcome == KeyedCreateOutcome.NotFound)
            {
                return Error(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Project {projectId} was not found.");
            }

            var created = result.Outcome == KeyedCreateOutcome.Created;
            var answer = await EpicPlans.ResultAsync(dbContext, result.Id!.Value, created, cancellationToken);
            if (answer is null)
            {
                // The epic of the earlier import was deleted between the look-up of the key and this read.
                return Error(StatusCodes.Status409Conflict, KanbanErrorCodes.Conflict,
                    "The epic this idempotency key imported was deleted while the call ran. Nothing was created; repeat the call to import the plan again.");
            }

            if (!created)
            {
                return Results.Ok(answer);
            }

            changeNotifier.Publish();
            return Results.Created($"/api/epics/{answer.EpicId}", answer);
        })
        .WithName("ImportEpicPlan");

        return endpoints;
    }

    // The body, or null when it is longer than the largest plan. A request need not say how long it is.
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > EpicPlanLimits.MaxRequestBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > EpicPlanLimits.MaxRequestBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IResult InvalidArgument(string message) =>
        Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, message);

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
