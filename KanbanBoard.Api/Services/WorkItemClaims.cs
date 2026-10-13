using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public enum WorkItemClaimOutcome
{
    Succeeded,
    NotFound,
    // A live claim is held by another claimant or under another marker.
    AlreadyClaimed,
    // The caller holds no live claim under this marker (it lapsed, was released, or never existed).
    NotHeld
}

/// <param name="Current">The stored item after the attempt, or null when it does not exist.</param>
public sealed record WorkItemClaimResult(WorkItemClaimOutcome Outcome, WorkItem? Current);

/// <summary>
/// Claim writes. Each is a single conditional UPDATE, so the database decides the winner: two concurrent claims
/// cannot both match the WHERE clause. Do not replace these with a read followed by SaveChanges.
///
/// Around that UPDATE runs a transaction that first holds the item's row and reads the claim stored on it, so that
/// the entry of the change feed reports the claim the UPDATE replaced. A write that is refused commits nothing.
/// </summary>
public static class WorkItemClaims
{
    public static Task<WorkItemClaimResult> ClaimAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        AppUser claimant,
        string marker,
        TimeSpan ttl,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid? claimantId = claimant.Id;
        DateTimeOffset? expiresAtUtc = now.Add(ttl);
        var claimantName = ClaimantNameFor(claimant);

        return WriteAsync(
            dbContext,
            itemId,
            claimant,
            WorkItemChangeKinds.Claimed,
            now,
            () => dbContext.WorkItems
                .Where(item => item.Id == itemId &&
                    (item.ClaimantUserId == null ||
                     item.ClaimExpiresAtUtc == null ||
                     item.ClaimExpiresAtUtc <= now ||
                     (item.ClaimantUserId == claimantId && item.ClaimMarker == marker)))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.ClaimantUserId, claimantId)
                        .SetProperty(item => item.ClaimantName, claimantName)
                        .SetProperty(item => item.ClaimMarker, marker)
                        .SetProperty(item => item.ClaimExpiresAtUtc, expiresAtUtc),
                    cancellationToken),
            // The UPDATE matched nothing, so a live claim of someone else is on the item.
            _ => WorkItemClaimOutcome.AlreadyClaimed,
            cancellationToken);
    }

    public static Task<WorkItemClaimResult> RenewAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        AppUser claimant,
        string marker,
        TimeSpan ttl,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid? claimantId = claimant.Id;
        DateTimeOffset? expiresAtUtc = now.Add(ttl);

        return WriteAsync(
            dbContext,
            itemId,
            claimant,
            WorkItemChangeKinds.ClaimRenewed,
            now,
            () => dbContext.WorkItems
                .Where(item => item.Id == itemId &&
                    item.ClaimantUserId == claimantId &&
                    item.ClaimMarker == marker &&
                    item.ClaimExpiresAtUtc > now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.ClaimExpiresAtUtc, expiresAtUtc),
                    cancellationToken),
            current => NotHeldOutcome(current, now),
            cancellationToken);
    }

    public static Task<WorkItemClaimResult> ReleaseAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        AppUser claimant,
        string marker,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid? claimantId = claimant.Id;

        // No expiry condition: the holder may clear its own claim after it lapsed.
        return WriteAsync(
            dbContext,
            itemId,
            claimant,
            WorkItemChangeKinds.ClaimReleased,
            now,
            () => dbContext.WorkItems
                .Where(item => item.Id == itemId &&
                    item.ClaimantUserId == claimantId &&
                    item.ClaimMarker == marker)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.ClaimantUserId, (Guid?)null)
                        .SetProperty(item => item.ClaimantName, (string?)null)
                        .SetProperty(item => item.ClaimMarker, (string?)null)
                        .SetProperty(item => item.ClaimExpiresAtUtc, (DateTimeOffset?)null),
                    cancellationToken),
            current => NotHeldOutcome(current, now),
            cancellationToken);
    }

    public static string ClaimantNameFor(AppUser claimant)
    {
        var name = !string.IsNullOrWhiteSpace(claimant.DisplayName) ? claimant.DisplayName.Trim()
            : !string.IsNullOrWhiteSpace(claimant.Email) ? claimant.Email.Trim()
            : claimant.Id.ToString();
        return name.Length <= WorkItemClaimLimits.MaxClaimantNameLength
            ? name
            : name[..WorkItemClaimLimits.MaxClaimantNameLength];
    }

    private static async Task<WorkItemClaimResult> WriteAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        AppUser claimant,
        string changeKind,
        DateTimeOffset now,
        Func<Task<int>> update,
        Func<WorkItem, WorkItemClaimOutcome> refusal,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await WorkItemChangeLog.LockItemAsync(dbContext, itemId, cancellationToken);
        // The item alone: its claim is all that is needed of it, here and for a refusal.
        var before = await dbContext.WorkItems.AsNoTracking().FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);
        if (before is null)
        {
            return new(WorkItemClaimOutcome.NotFound, null);
        }

        if (await update() == 0)
        {
            // Nothing was written, and leaving the transaction uncommitted records nothing.
            return new(refusal(before), before);
        }

        var current = (await LoadAsync(dbContext, itemId, cancellationToken))!;
        await WorkItemChangeLog.CommitAsync(
            dbContext,
            transaction,
            WorkItemChangeLog.Entry(itemId, current.ProjectId, changeKind, ChangeActor.From(claimant), now, StoredClaim(before), StoredClaim(current)),
            cancellationToken);
        return new(WorkItemClaimOutcome.Succeeded, current);
    }

    // The UPDATE matched nothing, so any live claim still on the item belongs to someone else.
    private static WorkItemClaimOutcome NotHeldOutcome(WorkItem current, DateTimeOffset now) =>
        current.ClaimExpiresAtUtc > now ? WorkItemClaimOutcome.AlreadyClaimed : WorkItemClaimOutcome.NotHeld;

    private static Dictionary<string, object?> StoredClaim(WorkItem item) =>
        WorkItemChangeLog.ClaimSnapshot(item.ClaimantUserId, item.ClaimantName, item.ClaimMarker, item.ClaimExpiresAtUtc);

    private static Task<WorkItem?> LoadAsync(KanbanDbContext dbContext, Guid itemId, CancellationToken cancellationToken) =>
        dbContext.WorkItems
            .AsNoTracking()
            .Include(item => item.Epic)
            .Include(item => item.Comments)
            .FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);
}
