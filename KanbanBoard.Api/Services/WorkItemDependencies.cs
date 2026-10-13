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
    Cycle,
    // Another caller held the dependency lock for the whole wait; nothing was read or written.
    Busy
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
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Two adds can each pass the cycle check and together close a loop, even when they share no item, so the
        // check and the insert run one caller at a time. Do not replace this with a plain read followed by a write.
        // The add holds the rows of two items, in the order of their keys; a delete of a project or an archive of an
        // epic holds its items in the order of an index, and MariaDB then rolls one of the two back (error 1213).
        // The add is run again, still under the lock.
        return await MariaDbNamedLock.RunAsync(dbContext, LockName, LockTimeoutSeconds, WorkItemDependencyOutcome.Busy, () =>
            WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                var (outcome, added) = await AddLockedAsync(dbContext, blockedItemId, blockerItemId, cancellationToken);
                if (added)
                {
                    await CommitAsync(dbContext, transaction, WorkItemChangeKinds.DependencyAdded, blockedItemId, blockerItemId, actor, now, cancellationToken);
                }

                return outcome;
            }), cancellationToken);
    }

    // Whether the edge was inserted: an edge that was already there succeeds without a write.
    private static async Task<(WorkItemDependencyOutcome Outcome, bool Added)> AddLockedAsync(
        KanbanDbContext dbContext,
        Guid blockedItemId,
        Guid blockerItemId,
        CancellationToken cancellationToken)
    {
        // Must be the first statement of the transaction. It changes nothing and holds the rows of both items, in
        // the order of their keys. Without it the INSERT below takes a shared lock on each of the two rows for its
        // foreign keys, one after the other, and deadlocks on MariaDB (error 1213) with a move that is renumbering
        // the same two items in the other order.
        await dbContext.WorkItems
            .Where(item => item.Id == blockedItemId || item.Id == blockerItemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), cancellationToken);

        if (!await BothExistAsync(dbContext, blockedItemId, blockerItemId, cancellationToken))
        {
            return (WorkItemDependencyOutcome.NotFound, false);
        }

        if (blockedItemId == blockerItemId ||
            await IsBlockedByAsync(dbContext, blockerItemId, blockedItemId, cancellationToken))
        {
            return (WorkItemDependencyOutcome.Cycle, false);
        }

        var exists = await dbContext.WorkItemDependencies.AnyAsync(
            dependency => dependency.BlockedItemId == blockedItemId && dependency.BlockerItemId == blockerItemId,
            cancellationToken);
        if (exists)
        {
            return (WorkItemDependencyOutcome.Succeeded, false);
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
                return (WorkItemDependencyOutcome.NotFound, false);
            }

            throw;
        }

        return (WorkItemDependencyOutcome.Succeeded, true);
    }

    /// <summary>Deletes the edge. Removing an edge that does not exist succeeds and changes nothing.</summary>
    public static async Task<WorkItemDependencyOutcome> RemoveAsync(
        KanbanDbContext dbContext,
        Guid blockedItemId,
        Guid blockerItemId,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The blocked item's row is held first, like every write that records an entry for an item: the removes of
        // one edge then run one at a time, and the second one reads the edge only after the first has committed.
        // The DELETE takes the edge's row and then its entry in the index on the blocker; a delete of the blocking
        // item or of its project holds the edges it removes through that index first, and MariaDB then rolls one of
        // the two back (error 1213). The remove is run again.
        return await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await WorkItemChangeLog.LockItemAsync(dbContext, blockedItemId, cancellationToken);
            if (!await BothExistAsync(dbContext, blockedItemId, blockerItemId, cancellationToken))
            {
                return WorkItemDependencyOutcome.NotFound;
            }

            // A tracked delete, not ExecuteDelete: MySql.EntityFrameworkCore writes that as DELETE FROM ... AS `w`,
            // which MariaDB rejects.
            var edge = await dbContext.WorkItemDependencies.FindAsync([blockedItemId, blockerItemId], cancellationToken);
            if (edge is not null)
            {
                dbContext.WorkItemDependencies.Remove(edge);
                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another caller removed the edge first: this call changed nothing and records nothing.
                    dbContext.ChangeTracker.Clear();
                    return WorkItemDependencyOutcome.Succeeded;
                }

                await CommitAsync(dbContext, transaction, WorkItemChangeKinds.DependencyRemoved, blockedItemId, blockerItemId, actor, now, cancellationToken);
            }

            return WorkItemDependencyOutcome.Succeeded;
        });
    }

    // One edge is one entry of the change feed, on the blocked item: the edge is that item's "blocked by".
    private static async Task CommitAsync(
        KanbanDbContext dbContext,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string changeKind,
        Guid blockedItemId,
        Guid blockerItemId,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var edge = new Dictionary<string, object?> { ["blocked_by"] = blockerItemId };
        var added = changeKind == WorkItemChangeKinds.DependencyAdded;
        await WorkItemChangeLog.CommitAsync(
            dbContext,
            transaction,
            WorkItemChangeLog.Entry(
                blockedItemId,
                await WorkItemChangeLog.ProjectIdAsync(dbContext, blockedItemId, cancellationToken),
                changeKind,
                actor,
                now,
                added ? null : edge,
                added ? edge : null),
            cancellationToken);
    }

    /// <summary>
    /// Holds the edges whose blocking item a delete is about to remove, until the transaction ends, and changes
    /// nothing. The database removes those edges with the item (ON DELETE CASCADE) without a word; the delete records
    /// them (see <see cref="RemovedWithBlockersAsync"/>). Call it before the first read of the transaction: a remove
    /// of one of these edges that commits later than that read would otherwise be recorded twice.
    /// </summary>
    public static Task HoldAsync(IQueryable<WorkItemDependency> edges, CancellationToken cancellationToken) =>
        edges.ExecuteUpdateAsync(setters => setters.SetProperty(edge => edge.BlockerItemId, edge => edge.BlockerItemId), cancellationToken);

    /// <summary>
    /// <see cref="HoldAsync"/> for the edges whose blocking item belongs to the project, for a delete of the project.
    /// On MariaDB the statement is written here: MySql.EntityFrameworkCore writes the UPDATE of a join as
    /// <c>UPDATE ... SET ... FROM</c>, which MariaDB rejects. STRAIGHT_JOIN makes it start from the project's items,
    /// whose rows the delete already holds, and reach the edges through the index on their blocker: no other edge
    /// and no item of another project is read, so none is locked.
    /// </summary>
    public static Task HoldBlockedByProjectAsync(KanbanDbContext dbContext, Guid projectId, CancellationToken cancellationToken) =>
        dbContext.Database.ProviderName == "MySql.EntityFrameworkCore"
            ? dbContext.Database.ExecuteSqlAsync(
                $"UPDATE `WorkItems` AS `i` STRAIGHT_JOIN `WorkItemDependencies` AS `d` ON `d`.`BlockerItemId` = `i`.`Id` SET `d`.`BlockerItemId` = `d`.`BlockerItemId` WHERE `i`.`ProjectId` = {projectId.ToString()}",
                cancellationToken)
            : HoldAsync(dbContext.WorkItemDependencies.Where(edge => edge.BlockerItem!.ProjectId == projectId), cancellationToken);

    /// <summary>
    /// One <c>dependency_removed</c> entry for each of <paramref name="edges"/> (held by <see cref="HoldAsync"/>)
    /// whose blocked item is not deleted with its blocker: on that blocked item, in its own project.
    /// </summary>
    /// <param name="deletedProjectId">The project that is being deleted, whose items need no entry of this kind.</param>
    public static async Task<List<WorkItemChange>> RemovedWithBlockersAsync(
        IQueryable<WorkItemDependency> edges,
        Guid? deletedProjectId,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var removed = await edges
            .AsNoTracking()
            .Select(edge => new { edge.BlockedItemId, edge.BlockerItemId, edge.BlockedItem!.ProjectId })
            .ToListAsync(cancellationToken);
        return removed
            .Where(edge => edge.ProjectId != deletedProjectId)
            .OrderBy(edge => edge.BlockedItemId)
            .ThenBy(edge => edge.BlockerItemId)
            .Select(edge => WorkItemChangeLog.Entry(
                edge.BlockedItemId,
                edge.ProjectId,
                WorkItemChangeKinds.DependencyRemoved,
                actor,
                now,
                new Dictionary<string, object?> { ["blocked_by"] = edge.BlockerItemId },
                null))
            .ToList();
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

    // One query per visited item: MySql.EntityFrameworkCore cannot translate Contains over a list of ids.
    private static async Task<bool> IsBlockedByAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        Guid candidateBlockerId,
        CancellationToken cancellationToken) =>
        await IsBlockedByAsync(
            itemId,
            candidateBlockerId,
            async current => await dbContext.WorkItemDependencies
                .Where(dependency => dependency.BlockedItemId == current)
                .Select(dependency => dependency.BlockerItemId)
                .ToListAsync(cancellationToken));

    /// <summary>
    /// Whether <paramref name="item"/> is blocked by <paramref name="candidateBlocker"/>, directly or through other
    /// items: the one rule for a cycle. Making the candidate blocked by the item would then close a loop.
    /// <paramref name="blockersOf"/> gives the items that directly block an item: the stored edges for an add, the
    /// plan's own edges for an import, whose items are all new.
    /// </summary>
    public static async ValueTask<bool> IsBlockedByAsync<TItem>(
        TItem item,
        TItem candidateBlocker,
        Func<TItem, ValueTask<IReadOnlyCollection<TItem>>> blockersOf)
        where TItem : notnull
    {
        var seen = new HashSet<TItem> { item };
        var pending = new Stack<TItem>([item]);
        while (pending.TryPop(out var current))
        {
            foreach (var blocker in await blockersOf(current))
            {
                if (EqualityComparer<TItem>.Default.Equals(blocker, candidateBlocker))
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
