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
/// </summary>
public static class WorkItemClaims
{
    public static async Task<WorkItemClaimResult> ClaimAsync(
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

        var updated = await dbContext.WorkItems
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
                cancellationToken);

        var current = await LoadAsync(dbContext, itemId, cancellationToken);
        if (current is null)
        {
            return new(WorkItemClaimOutcome.NotFound, null);
        }

        return new(updated > 0 ? WorkItemClaimOutcome.Succeeded : WorkItemClaimOutcome.AlreadyClaimed, current);
    }

    public static async Task<WorkItemClaimResult> RenewAsync(
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

        var updated = await dbContext.WorkItems
            .Where(item => item.Id == itemId &&
                item.ClaimantUserId == claimantId &&
                item.ClaimMarker == marker &&
                item.ClaimExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.ClaimExpiresAtUtc, expiresAtUtc),
                cancellationToken);

        return await ResultForHeldClaimAsync(dbContext, itemId, updated, now, cancellationToken);
    }

    public static async Task<WorkItemClaimResult> ReleaseAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        AppUser claimant,
        string marker,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid? claimantId = claimant.Id;

        // No expiry condition: the holder may clear its own claim after it lapsed.
        var updated = await dbContext.WorkItems
            .Where(item => item.Id == itemId &&
                item.ClaimantUserId == claimantId &&
                item.ClaimMarker == marker)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.ClaimantUserId, (Guid?)null)
                    .SetProperty(item => item.ClaimantName, (string?)null)
                    .SetProperty(item => item.ClaimMarker, (string?)null)
                    .SetProperty(item => item.ClaimExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken);

        return await ResultForHeldClaimAsync(dbContext, itemId, updated, now, cancellationToken);
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

    private static async Task<WorkItemClaimResult> ResultForHeldClaimAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        int updated,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await LoadAsync(dbContext, itemId, cancellationToken);
        if (current is null)
        {
            return new(WorkItemClaimOutcome.NotFound, null);
        }

        if (updated > 0)
        {
            return new(WorkItemClaimOutcome.Succeeded, current);
        }

        // The UPDATE matched nothing, so any live claim still on the item belongs to someone else.
        return new(
            current.ClaimExpiresAtUtc > now ? WorkItemClaimOutcome.AlreadyClaimed : WorkItemClaimOutcome.NotHeld,
            current);
    }

    private static Task<WorkItem?> LoadAsync(KanbanDbContext dbContext, Guid itemId, CancellationToken cancellationToken) =>
        dbContext.WorkItems
            .AsNoTracking()
            .Include(item => item.Epic)
            .Include(item => item.Comments)
            .FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);
}
