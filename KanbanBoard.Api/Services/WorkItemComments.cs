using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public enum WorkItemCommentOutcome
{
    Succeeded,
    /// <summary>The idempotency key already belongs to a comment of the item: nothing was written.</summary>
    Unchanged,
    NotFound
}

/// <param name="CommentId">The comment that was added, or the one the idempotency key already belonged to.</param>
public sealed record WorkItemCommentResult(WorkItemCommentOutcome Outcome, Guid? CommentId = null);

/// <summary>
/// Comment writes and the paging order of a work item's comments. Everything here runs on SQLite in the tests and on
/// MariaDB through MySql.EntityFrameworkCore in production.
/// </summary>
public static class WorkItemComments
{
    /// <summary>
    /// Adds a comment to the item and stamps the item's UpdatedAtUtc with the comment's time. With an
    /// <paramref name="idempotencyKey"/> that <paramref name="authorUserId"/> already used on this item, nothing is
    /// written and the earlier comment is reported, whatever its body.
    /// </summary>
    public static async Task<WorkItemCommentResult> AddAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        Guid? authorUserId,
        string author,
        string body,
        string? idempotencyKey,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // MariaDB keeps microseconds: the comment's time must be the value the cursor of a page will read back.
        var createdAtUtc = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAndStampItemAsync(dbContext, itemId, createdAtUtc, cancellationToken))
        {
            return new(WorkItemCommentOutcome.NotFound);
        }

        var keyHash = idempotencyKey is null ? null : IdempotencyKeys.Hash(authorUserId, idempotencyKey);
        if (keyHash is not null)
        {
            var earlier = await dbContext.WorkItemComments
                .AsNoTracking()
                .FirstOrDefaultAsync(comment => comment.WorkItemId == itemId && comment.IdempotencyKeyHash == keyHash, cancellationToken);
            if (earlier is not null)
            {
                // Nothing changed: leaving the transaction uncommitted takes the stamp back.
                return new(WorkItemCommentOutcome.Unchanged, earlier.Id);
            }
        }

        // Every comment of an item is later than the ones committed before it, whatever the clocks of the writers
        // say. A reader paging through the comments therefore never has a new one land behind its cursor.
        var latest = await dbContext.WorkItemComments
            .AsNoTracking()
            .Where(comment => comment.WorkItemId == itemId)
            .OrderByDescending(comment => comment.CreatedAtUtc)
            .Select(comment => new { comment.CreatedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && latest.CreatedAtUtc >= createdAtUtc)
        {
            createdAtUtc = latest.CreatedAtUtc.AddTicks(TimeSpan.TicksPerMicrosecond);
            await LockAndStampItemAsync(dbContext, itemId, createdAtUtc, cancellationToken);
        }

        var added = new WorkItemComment
        {
            WorkItemId = itemId,
            Author = author,
            AuthorUserId = authorUserId,
            Body = body,
            IdempotencyKeyHash = keyHash,
            CreatedAtUtc = createdAtUtc
        };
        dbContext.WorkItemComments.Add(added);
        await dbContext.SaveChangesAsync(cancellationToken);
        await WorkItemChangeLog.CommitAsync(
            dbContext,
            transaction,
            WorkItemChangeLog.Entry(
                itemId,
                await WorkItemChangeLog.ProjectIdAsync(dbContext, itemId, cancellationToken),
                WorkItemChangeKinds.CommentAdded,
                actor,
                createdAtUtc,
                null,
                new Dictionary<string, object?> { ["comment"] = new { added.Id, added.Author, added.Body } }),
            cancellationToken);
        return new(WorkItemCommentOutcome.Succeeded, added.Id);
    }

    // Must be the first statement of the transaction, before the INSERT. The UPDATE holds the item's row until the
    // transaction ends, so the comment writes of one item run one at a time: the second of two adds with one
    // idempotency key waits here and then finds the first one's comment, instead of failing on the unique index.
    // In the other order two writers of one item deadlock on MariaDB: each INSERT holds a shared lock on the item's
    // row (its foreign key) while the UPDATE of the other waits for it. Do not replace it with a plain read.
    private static async Task<bool> LockAndStampItemAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.WorkItems
            .Where(item => item.Id == itemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, now), cancellationToken) > 0;

    /// <summary>The paging order: oldest first. Neither column ever changes.</summary>
    public static IQueryable<WorkItemComment> InPagingOrder(this IQueryable<WorkItemComment> comments) =>
        comments.OrderBy(comment => comment.CreatedAtUtc).ThenBy(comment => comment.Id);

    /// <summary>Keeps the comments that come after <paramref name="cursor"/> in the paging order.</summary>
    public static IQueryable<WorkItemComment> WhereAfter(this IQueryable<WorkItemComment> comments, WorkItemCursor cursor)
    {
        var createdAtUtc = cursor.CreatedAtUtc;
        var id = cursor.Id;
        return comments.Where(comment =>
            comment.CreatedAtUtc > createdAtUtc ||
            (comment.CreatedAtUtc == createdAtUtc && comment.Id.CompareTo(id) > 0));
    }
}
