using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Api.Services;

public static class KanbanCommentEndpointRouteBuilderExtensions
{
    public const string InactiveTokenMessage =
        "The forwarded personal access token is revoked or expired and cannot be used to comment.";

    public static IEndpointRouteBuilder MapKanbanCommentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Error messages name the arguments as the MCP tools spell them.
        endpoints.MapPost("/api/items/{itemId:guid}/comments", async (
            Guid itemId,
            CreateWorkItemCommentRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            IOptions<AppOptions> appOptions,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();

            // A comment written with a token is its identity's: no request field can name another author.
            AppUser? identity = null;
            if (KanbanInternalApiExtensions.IsInternalApiRequest(httpContext) &&
                httpContext.User.FindFirst(KanbanInternalAuth.PersonalAccessTokenIdHeaderName) is not null)
            {
                identity = await KanbanClaimEndpointRouteBuilderExtensions.ResolveClaimantAsync(httpContext, dbContext, now, cancellationToken);
                if (identity is null)
                {
                    return Error(StatusCodes.Status403Forbidden, KanbanErrorCodes.ScopeDenied, InactiveTokenMessage);
                }
            }

            var author = identity is null ? request.Author?.Trim() : WorkItemClaims.ClaimantNameFor(identity);
            if (string.IsNullOrEmpty(author) || author.Length > WorkItemCommentLimits.MaxAuthorLength)
            {
                return InvalidArgument($"Argument 'author' is required and must be at most {WorkItemCommentLimits.MaxAuthorLength} characters.");
            }

            var body = request.Body?.Trim();
            if (string.IsNullOrEmpty(body))
            {
                return InvalidArgument("Argument 'body' is required.");
            }

            if (request.IdempotencyKey is not null && !IdempotencyKeys.IsValid(request.IdempotencyKey))
            {
                return InvalidArgument(IdempotencyKeys.InvalidMessage);
            }

            var actor = identity is null ? await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken) : ChangeActor.From(identity);
            var result = await WorkItemComments.AddAsync(
                dbContext, itemId, identity?.Id, author, body, request.IdempotencyKey, actor, now, cancellationToken);
            var item = result.Outcome == WorkItemCommentOutcome.NotFound
                ? null
                : await dbContext.WorkItems
                    .AsNoTracking()
                    .Include(workItem => workItem.Epic)
                    .Include(workItem => workItem.Comments)
                    .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
            if (item is null)
            {
                return NotFound(itemId);
            }

            var created = result.Outcome == WorkItemCommentOutcome.Succeeded;
            if (created)
            {
                changeNotifier.Publish();
            }

            var dto = WorkItemDtoMapper.ToDto(
                item,
                now,
                appOptions.Value,
                await WorkItemDependencies.LoadAsync(dbContext, itemId, cancellationToken),
                await WorkItemLinks.LoadAsync(dbContext, itemId, cancellationToken)) with
            {
                CommentId = result.CommentId,
                CommentCreated = created
            };
            return created
                ? Results.Created($"/api/items/{itemId}/comments/{result.CommentId}", dto)
                : Results.Ok(dto);
        })
        .WithName("CreateWorkItemComment");

        // One page of the item's comments, oldest first.
        endpoints.MapGet("/api/items/{itemId:guid}/comments", async (
            Guid itemId,
            int? limit,
            string? cursor,
            KanbanDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (limit is < 1 or > WorkItemListing.MaxPageSize)
            {
                return InvalidArgument($"Argument 'limit' must be between 1 and {WorkItemListing.MaxPageSize}.");
            }

            WorkItemCursor after = default;
            if (cursor is not null && !WorkItemCursor.TryDecode(cursor, WorkItemCursor.Comments, out after))
            {
                return InvalidArgument("Argument 'cursor' is not a cursor returned by this tool. Pass next_cursor from the previous page unchanged.");
            }

            if (!await dbContext.WorkItems.AnyAsync(item => item.Id == itemId, cancellationToken))
            {
                return NotFound(itemId);
            }

            var query = dbContext.WorkItemComments.AsNoTracking().Where(comment => comment.WorkItemId == itemId);
            if (cursor is not null)
            {
                query = query.WhereAfter(after);
            }

            // One row more than the page, to know whether another page follows.
            var pageSize = limit ?? WorkItemListing.DefaultPageSize;
            var rows = await query.InPagingOrder().Take(pageSize + 1).ToListAsync(cancellationToken);
            var page = rows.Take(pageSize).ToList();

            return Results.Ok(new WorkItemCommentPageDto(
                page.Select(WorkItemDtoMapper.ToCommentDto).ToList(),
                rows.Count > pageSize ? new WorkItemCursor(page[^1].CreatedAtUtc, page[^1].Id).Encode(WorkItemCursor.Comments) : null));
        })
        .WithName("ListWorkItemComments");

        return endpoints;
    }

    private static IResult NotFound(Guid itemId) =>
        Error(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Work item {itemId} was not found.");

    private static IResult InvalidArgument(string message) =>
        Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, message);

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
