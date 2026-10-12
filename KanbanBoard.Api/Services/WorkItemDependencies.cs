using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public enum WorkItemDependencyOutcome
{
    Succeeded,
    NotFound,
    // The edge would make an item block itself, directly or through other items.
    Cycle
}

/// <summary>Blockers and dependents of a set of work items, keyed by work item id.</summary>
public sealed record WorkItemDependencyLookup(
    ILookup<Guid, WorkItemDependencyRefDto> Blockers,
    ILookup<Guid, WorkItemDependencyRefDto> Dependents);

public static class WorkItemDependencies
{
    public const string LockName = "kanban_work_item_dependencies";
    public const int LockTimeoutSeconds = 10;

    /// <summary>
    /// Records that <paramref name="blockedItemId"/> is blocked by <paramref name="blockerItemId"/>. An edge that
    /// already exists is left as it is. The items may belong to different projects.
    /// </summary>
    public static async Task<WorkItemDependencyOutcome> AddAsync(
        KanbanDbContext dbContext,
        Guid blockedItemId,
        Guid blockerItemId,
        CancellationToken cancellationToken)
    {
        // Two adds can each pass the cycle check and together close a loop, even when they share no item, so the
        // check and the insert run one caller at a time. Do not replace this with a plain read followed by a write.
        // SQLite: the transaction is BEGIN IMMEDIATE, which admits one writer. MariaDB: a named lock, taken before
        // the transaction starts so its reads see every edge committed by earlier holders.
        // The connection is opened here so that it stays the same one from GET_LOCK to RELEASE_LOCK: left to
        // itself EF returns it to the pool at commit, and the lock would stay with the pooled connection.
        var useNamedLock = dbContext.Database.ProviderName == "MySql.EntityFrameworkCore";
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (useNamedLock)
            {
                var acquired = await dbContext.Database
                    .SqlQuery<long?>($"SELECT CAST(GET_LOCK({LockName}, {LockTimeoutSeconds}) AS SIGNED) AS `Value`")
                    .ToListAsync(cancellationToken);
                if (acquired.Single() != 1)
                {
                    throw new TimeoutException($"Could not acquire the '{LockName}' lock within {LockTimeoutSeconds} s.");
                }
            }

            try
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                var outcome = await AddLockedAsync(dbContext, blockedItemId, blockerItemId, cancellationToken);
                if (outcome == WorkItemDependencyOutcome.Succeeded)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return outcome;
            }
            finally
            {
                if (useNamedLock)
                {
                    // Not cancellable: the lock must go even when the request was aborted.
                    await dbContext.Database
                        .SqlQuery<long?>($"SELECT CAST(RELEASE_LOCK({LockName}) AS SIGNED) AS `Value`")
                        .ToListAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task<WorkItemDependencyOutcome> AddLockedAsync(
        KanbanDbContext dbContext,
        Guid blockedItemId,
        Guid blockerItemId,
        CancellationToken cancellationToken)
    {
        if (!await BothExistAsync(dbContext, blockedItemId, blockerItemId, cancellationToken))
        {
            return WorkItemDependencyOutcome.NotFound;
        }

        if (blockedItemId == blockerItemId ||
            await IsBlockedByAsync(dbContext, blockerItemId, blockedItemId, cancellationToken))
        {
            return WorkItemDependencyOutcome.Cycle;
        }

        var exists = await dbContext.WorkItemDependencies.AnyAsync(
            dependency => dependency.BlockedItemId == blockedItemId && dependency.BlockerItemId == blockerItemId,
            cancellationToken);
        if (exists)
        {
            return WorkItemDependencyOutcome.Succeeded;
        }

        dbContext.WorkItemDependencies.Add(new WorkItemDependency
        {
            BlockedItemId = blockedItemId,
            BlockerItemId = blockerItemId
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // An item deleted since the check above fails the foreign key.
            dbContext.ChangeTracker.Clear();
            if (!await BothExistAsync(dbContext, blockedItemId, blockerItemId, cancellationToken))
            {
                return WorkItemDependencyOutcome.NotFound;
            }

            throw;
        }

        return WorkItemDependencyOutcome.Succeeded;
    }

    /// <summary>Deletes the edge. Removing an edge that does not exist succeeds and changes nothing.</summary>
    public static async Task<WorkItemDependencyOutcome> RemoveAsync(
        KanbanDbContext dbContext,
        Guid blockedItemId,
        Guid blockerItemId,
        CancellationToken cancellationToken)
    {
        if (!await BothExistAsync(dbContext, blockedItemId, blockerItemId, cancellationToken))
        {
            return WorkItemDependencyOutcome.NotFound;
        }

        await dbContext.WorkItemDependencies
            .Where(dependency => dependency.BlockedItemId == blockedItemId && dependency.BlockerItemId == blockerItemId)
            .ExecuteDeleteAsync(cancellationToken);
        return WorkItemDependencyOutcome.Succeeded;
    }

    /// <summary>Keeps only items whose blockers are all Done (items without blockers included).</summary>
    public static IQueryable<WorkItem> WhereUnblocked(this IQueryable<WorkItem> items, KanbanDbContext dbContext) =>
        items.Where(item => !dbContext.WorkItemDependencies.Any(dependency =>
            dependency.BlockedItemId == item.Id && dependency.BlockerItem!.Status != WorkItemStatus.Done));

    /// <summary>Loads the blockers and dependents of every item in <paramref name="items"/>, with their current status.</summary>
    public static async Task<WorkItemDependencyLookup> LoadAsync(
        KanbanDbContext dbContext,
        IQueryable<WorkItem> items,
        CancellationToken cancellationToken)
    {
        var edges = await dbContext.WorkItemDependencies
            .AsNoTracking()
            .Where(dependency =>
                items.Any(item => item.Id == dependency.BlockedItemId) ||
                items.Any(item => item.Id == dependency.BlockerItemId))
            .Select(dependency => new
            {
                dependency.BlockedItemId,
                BlockedTitle = dependency.BlockedItem!.Title,
                BlockedStatus = dependency.BlockedItem.Status,
                dependency.BlockerItemId,
                BlockerTitle = dependency.BlockerItem!.Title,
                BlockerStatus = dependency.BlockerItem.Status
            })
            .ToListAsync(cancellationToken);

        return new WorkItemDependencyLookup(
            edges
                .OrderBy(edge => edge.BlockerTitle, StringComparer.Ordinal)
                .ThenBy(edge => edge.BlockerItemId)
                .ToLookup(
                    edge => edge.BlockedItemId,
                    edge => new WorkItemDependencyRefDto(edge.BlockerItemId, edge.BlockerTitle, edge.BlockerStatus)),
            edges
                .OrderBy(edge => edge.BlockedTitle, StringComparer.Ordinal)
                .ThenBy(edge => edge.BlockedItemId)
                .ToLookup(
                    edge => edge.BlockerItemId,
                    edge => new WorkItemDependencyRefDto(edge.BlockedItemId, edge.BlockedTitle, edge.BlockedStatus)));
    }

    public static Task<WorkItemDependencyLookup> LoadAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        CancellationToken cancellationToken) =>
        LoadAsync(dbContext, dbContext.WorkItems.Where(item => item.Id == itemId), cancellationToken);

    private static async Task<bool> BothExistAsync(
        KanbanDbContext dbContext,
        Guid firstItemId,
        Guid secondItemId,
        CancellationToken cancellationToken)
    {
        var found = await dbContext.WorkItems
            .CountAsync(item => item.Id == firstItemId || item.Id == secondItemId, cancellationToken);
        return found == (firstItemId == secondItemId ? 1 : 2);
    }

    // Walks the blockers of itemId looking for candidateBlockerId. One query per visited item:
    // MySql.EntityFrameworkCore cannot translate Contains over a list of ids.
    private static async Task<bool> IsBlockedByAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        Guid candidateBlockerId,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<Guid> { itemId };
        var pending = new Stack<Guid>([itemId]);
        while (pending.TryPop(out var current))
        {
            var blockers = await dbContext.WorkItemDependencies
                .Where(dependency => dependency.BlockedItemId == current)
                .Select(dependency => dependency.BlockerItemId)
                .ToListAsync(cancellationToken);
            foreach (var blocker in blockers)
            {
                if (blocker == candidateBlockerId)
                {
                    return true;
                }

                if (seen.Add(blocker))
                {
                    pending.Push(blocker);
                }
            }
        }

        return false;
    }
}
