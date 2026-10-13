using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace KanbanBoard.Api.Services;

public enum WorkItemWriteOutcome
{
    Succeeded,
    NotFound,
    /// <summary>The item no longer has the version the caller expected: nothing was written.</summary>
    VersionConflict,
    /// <summary>A field of the update is not acceptable: nothing was written.</summary>
    Invalid
}

/// <param name="Item">The item as it is after a write that succeeded, with its epic and comments.</param>
/// <param name="Message">Why the write was refused.</param>
public sealed record WorkItemWriteResult(WorkItemWriteOutcome Outcome, WorkItem? Item = null, string? Message = null);

/// <summary>
/// The writes to a work item's own fields, and the item's version. Everything here runs on SQLite in the tests and
/// on MariaDB through MySql.EntityFrameworkCore in production.
///
/// <see cref="WorkItem.Version"/> counts these writes: it is 1 on a new item and goes up by one with every update
/// and every move of the item, and when archiving or deleting its epic takes the item out of that epic. It does
/// not move when a sibling is reordered around the item, when the item is claimed, renewed or released, or when a
/// comment, link or dependency is added or removed: none of those can be overwritten by an update. The version is
/// only ever written as <c>Version + 1</c> inside an UPDATE, so no two writes give an item the same version.
/// </summary>
public static class WorkItemWrites
{
    // The unique index on the idempotency key, as the server names it in error 1062.
    public const string IdempotencyIndexName = "IX_WorkItems_ProjectId_IdempotencyKeyHash";

    public const string EpicRefusal = "Epic must belong to the same project as the work item and be active.";

    /// <summary>
    /// A new item with its fields as they are stored, or why they are refused. Its place in its column is not set:
    /// <see cref="CreateAsync"/> and the import of an epic plan give it one. Every way of creating an item goes
    /// through here, so they all apply the same rules.
    /// </summary>
    public static bool TryNew(
        Guid projectId,
        Guid? epicId,
        string? title,
        string? description,
        WorkItemType type,
        WorkItemStatus status,
        WorkItemPriority priority,
        int? estimate,
        string? labels,
        IReadOnlyList<string>? labelList,
        out WorkItem item,
        out string error)
    {
        var normalizedTitle = title?.Trim() ?? string.Empty;
        item = new WorkItem
        {
            ProjectId = projectId,
            EpicId = epicId,
            Title = normalizedTitle,
            Description = description?.Trim(),
            Type = type,
            Status = status,
            Priority = priority,
            Estimate = estimate,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        if (normalizedTitle.Length == 0)
        {
            error = "Argument 'title' is required.";
            return false;
        }

        if (normalizedTitle.Length > WorkItemLimits.MaxTitleLength)
        {
            error = $"Argument 'title' must be at most {WorkItemLimits.MaxTitleLength} characters.";
            return false;
        }

        if (!TryNormalizeLabels(labels, labelList, out var stored, out error))
        {
            return false;
        }

        item.Labels = stored;
        return true;
    }

    /// <summary>
    /// Stores a new item at the end of its column and records it in the change feed. With an
    /// <paramref name="idempotencyKeyHash"/> that already belongs to an item of the project, nothing is written, no
    /// entry is recorded and that item is reported, whatever its fields.
    ///
    /// With or without a key, the create starts by holding the project's row, like the import of an epic plan
    /// (<see cref="IdempotencyKeys.CreateOnceAsync"/>): the creates into one project run one at a time, so each
    /// reads the end of its column after the one before it has committed and no two of them take the same
    /// <see cref="WorkItem.Order"/>. Do not read the end of the column outside that transaction.
    /// </summary>
    public static async Task<KeyedCreateResult> CreateAsync(
        KanbanDbContext dbContext,
        WorkItem item,
        string? idempotencyKeyHash,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        async Task<KeyedCreateResult> InsertAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            dbContext.WorkItems.Add(item);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WorkItemChangeLog.CommitAsync(
                dbContext,
                transaction,
                WorkItemChangeLog.Entry(item.Id, item.ProjectId, WorkItemChangeKinds.ItemCreated, actor, now, null, WorkItemChangeLog.Snapshot(item)),
                cancellationToken);
            return new(KeyedCreateOutcome.Created, item.Id);
        }

        Task<bool> EpicIsUsableAsync() =>
            dbContext.Epics.AnyAsync(epic => epic.Id == item.EpicId && epic.ProjectId == item.ProjectId && !epic.IsArchived, cancellationToken);

        item.IdempotencyKeyHash = idempotencyKeyHash;
        return await IdempotencyKeys.CreateOnceAsync(
            dbContext,
            () => Projects.LockAsync(dbContext, item.ProjectId, cancellationToken),
            // A create without a key has no earlier item to find.
            async () => idempotencyKeyHash is null
                ? null
                : (await dbContext.WorkItems
                    .AsNoTracking()
                    .Where(candidate => candidate.ProjectId == item.ProjectId && candidate.IdempotencyKeyHash == idempotencyKeyHash)
                    .Select(candidate => new { candidate.Id })
                    .FirstOrDefaultAsync(cancellationToken))?.Id,
            async transaction =>
            {
                // After the look-up of the key: a repeat is not refused because its epic has been archived since.
                if (item.EpicId is not null && !await EpicIsUsableAsync())
                {
                    return new(KeyedCreateOutcome.Invalid, Message: EpicRefusal);
                }

                item.Order = await NextOrderAsync(dbContext, item.ProjectId, item.Status, cancellationToken);
                return await InsertAsync(transaction);
            },
            IdempotencyIndexName,
            cancellationToken);
    }

    /// <summary>
    /// Writes the set fields of <paramref name="patch"/> to the item, or writes nothing and says why. With an
    /// expected version, the item is written by one conditional UPDATE, so the database decides between concurrent
    /// updates: only one of them can match the version. Do not replace it with a read followed by SaveChanges.
    ///
    /// An update that names a status may put the item at the end of another column, so it starts by holding the
    /// project's row, like a create and a move (<see cref="HoldColumnsAsync"/>): it reads the end of that column
    /// after every earlier writer of the project's columns has committed.
    /// </summary>
    public static async Task<WorkItemWriteResult> UpdateAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        UpdateWorkItemPatch patch,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var title = patch.Title?.Trim();
        if (patch.HasTitle && (string.IsNullOrEmpty(title) || title.Length > WorkItemLimits.MaxTitleLength))
        {
            return Invalid($"Argument 'title' is required and must be at most {WorkItemLimits.MaxTitleLength} characters.");
        }

        if ((patch.HasType && patch.Type is null) || (patch.HasStatus && patch.Status is null) || (patch.HasPriority && patch.Priority is null))
        {
            var name = patch is { HasType: true, Type: null } ? "type" : patch is { HasStatus: true, Status: null } ? "status" : "priority";
            return Invalid($"Argument '{name}' cannot be emptied: every work item has one.");
        }

        string? labels = null;
        if (patch.HasLabels && !TryNormalizeLabels(patch.Labels, patch.LabelList, out labels, out var labelError))
        {
            return Invalid(labelError);
        }

        // Read before the transaction starts: a read inside it would come before the statement that holds the row.
        var projectId = patch.Status is null ? null : await ProjectOfAsync(dbContext, itemId, cancellationToken);

        // The save below can rewrite the item's entries in the indexes on its column and its epic while this
        // transaction holds the item's row; a delete of the project or an archive of the epic takes the two in the
        // other order, and MariaDB then rolls one of them back (error 1213). Everything of the attempt is rolled
        // back with it, the version and the entry of the change feed included, so the update is run again. So it
        // is when its save finds a row gone.
        return await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
        {
            // The UPDATE holds the item's row until the transaction ends: the updates of one item run one at a time,
            // and each reads the item only after the one before it has committed. Only the project's row is held
            // before it, and only by an update that names a status.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            if (projectId is not null)
            {
                await HoldColumnsAsync(dbContext, projectId.Value, cancellationToken);
            }

            var refusal = await AdvanceAsync(
                dbContext, itemId, patch.ExpectedVersion, setters => setters.SetProperty(item => item.UpdatedAtUtc, now), cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }

            var item = await dbContext.WorkItems
                .Include(workItem => workItem.Epic)
                .Include(workItem => workItem.Comments)
                .FirstAsync(workItem => workItem.Id == itemId, cancellationToken);

            // The row is held, so these are the values this update replaces. The version was advanced just above.
            var before = WorkItemChangeLog.Snapshot(item);
            before["version"] = item.Version - 1;

            // An epic is checked when the update names one: an update that leaves the epic alone is not refused
            // because that epic has been archived since.
            if (patch is { HasEpicId: true, EpicId: not null } &&
                !await dbContext.Epics.AnyAsync(epic => epic.Id == patch.EpicId && epic.ProjectId == item.ProjectId && !epic.IsArchived, cancellationToken))
            {
                // Leaving the transaction uncommitted takes the version and the stamp back.
                return Invalid(EpicRefusal);
            }

            if (patch.HasEpicId)
            {
                item.EpicId = patch.EpicId;
            }

            if (patch.HasTitle)
            {
                item.Title = title!;
            }

            if (patch.HasDescription)
            {
                item.Description = patch.Description?.Trim();
            }

            if (patch.HasEstimate)
            {
                item.Estimate = patch.Estimate;
            }

            if (patch.HasLabels)
            {
                item.Labels = labels;
            }

            item.Type = patch.Type ?? item.Type;
            item.Priority = patch.Priority ?? item.Priority;
            if (patch.Status is { } status && status != item.Status)
            {
                // A new column: the item goes to its end.
                item.Status = status;
                item.Order = await NextOrderAsync(dbContext, item.ProjectId, status, cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await WorkItemChangeLog.CommitAsync(
                dbContext,
                transaction,
                WorkItemChangeLog.Entry(item.Id, item.ProjectId, WorkItemChangeKinds.ItemUpdated, actor, now, before, WorkItemChangeLog.Snapshot(item)),
                cancellationToken);

            if (item.EpicId is null)
            {
                item.Epic = null;
            }
            else
            {
                await dbContext.Entry(item).Reference(workItem => workItem.Epic).LoadAsync(cancellationToken);
            }

            return new WorkItemWriteResult(WorkItemWriteOutcome.Succeeded, item);
        }, alsoWhenARowVanished: true);
    }

    /// <summary>
    /// Puts the item in a column at a position and renumbers the other items of that column around it, in one
    /// transaction. The item itself is written by one conditional UPDATE; the siblings keep their version and
    /// UpdatedAtUtc and get no entry in the change feed.
    ///
    /// The move starts by holding the project's row (<see cref="HoldColumnsAsync"/>), like a create, an import and an
    /// update that names a status, and like the deletes that take an item out of a column: every write that gives an
    /// item a place in a column of the project or takes one out runs one at a time, and reads the column after the
    /// one before it has committed. So no two items of a column end up with
    /// the same <see cref="WorkItem.Order"/>, and two moves in one column cannot deadlock over the rows of their
    /// siblings. The rows are taken in one order everywhere: the project, then items, then the lock of the change
    /// feed. Do not renumber outside this transaction, and do not read anything in it before the project is held.
    /// </summary>
    public static async Task<WorkItemWriteResult> MoveAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        WorkItemStatus status,
        int order,
        long? expectedVersion,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Read before the transaction starts: a read inside it would come before the statement that holds the row.
        if (await ProjectOfAsync(dbContext, itemId, cancellationToken) is not { } projectId)
        {
            return new(WorkItemWriteOutcome.NotFound);
        }

        // The move holds the rows of several items while other writers hold one of them (a delete of the project
        // or an archive of the epic takes them in another order), and MariaDB then rolls one of the two back
        // (error 1213). The whole attempt is rolled back, the version, the renumbering and the entry of the change
        // feed included, so the move is run again: it is recorded once and advances the version once. So it is
        // when a sibling it read was deleted before it was renumbered (the save then throws, and leaving the
        // transaction rolls everything back): every delete of an item holds the project first, like the move, so
        // that only happens when a row is deleted some other way.
        return await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await HoldColumnsAsync(dbContext, projectId, cancellationToken);
            // The UPDATE below overwrites the column and the position, so the item is held and read first: the
            // entry of the change feed reports the values this move replaces.
            await WorkItemChangeLog.LockItemAsync(dbContext, itemId, cancellationToken);
            var before = await dbContext.WorkItems.AsNoTracking().FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
            if (before is null)
            {
                return new(WorkItemWriteOutcome.NotFound);
            }

            var refusal = await AdvanceAsync(
                dbContext,
                itemId,
                expectedVersion,
                setters => setters
                    .SetProperty(item => item.Status, status)
                    .SetProperty(item => item.Order, order)
                    .SetProperty(item => item.UpdatedAtUtc, now),
                cancellationToken);
            if (refusal is not null)
            {
                // Leaving the transaction uncommitted: a refused move writes nothing, of the item or of a sibling.
                return refusal;
            }

            await NormalizeColumnOrderingAsync(dbContext, before.ProjectId, status, itemId, order, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Read while the UPDATE still holds the row, so the item that is returned carries this move's version.
            var written = await dbContext.WorkItems
                .AsNoTracking()
                .Include(workItem => workItem.Epic)
                .Include(workItem => workItem.Comments)
                .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
            if (written is null)
            {
                return new(WorkItemWriteOutcome.NotFound);
            }

            await WorkItemChangeLog.CommitAsync(
                dbContext,
                transaction,
                WorkItemChangeLog.Entry(itemId, written.ProjectId, WorkItemChangeKinds.ItemMoved, actor, now, WorkItemChangeLog.Snapshot(before), WorkItemChangeLog.Snapshot(written)),
                cancellationToken);
            return new WorkItemWriteResult(WorkItemWriteOutcome.Succeeded, written);
        }, alsoWhenARowVanished: true);
    }

    /// <summary>
    /// Takes every item out of the epic (or every unfinished one), as one UPDATE that also advances their versions.
    /// Returns one entry of the change feed for each item it took out, for the caller to pass to
    /// <see cref="WorkItemChangeLog.CommitAsync"/>. Call it first in the caller's transaction: it starts by holding
    /// the rows, so that the versions it reports are the ones it replaces.
    /// </summary>
    public static async Task<List<WorkItemChange>> DetachFromEpicAsync(
        KanbanDbContext dbContext,
        Guid epicId,
        bool unfinishedOnly,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var items = dbContext.WorkItems.Where(item => item.EpicId == epicId);
        if (unfinishedOnly)
        {
            items = items.Where(item => item.Status != WorkItemStatus.Done && item.Status != WorkItemStatus.Closed);
        }

        await items.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), cancellationToken);
        var held = await items
            .AsNoTracking()
            .Select(item => new { item.Id, item.ProjectId, item.Version })
            .ToListAsync(cancellationToken);

        await items.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.EpicId, (Guid?)null)
                .SetProperty(item => item.UpdatedAtUtc, now)
                .SetProperty(item => item.Version, item => item.Version + 1),
            cancellationToken);

        return held
            .Select(item => WorkItemChangeLog.Entry(
                item.Id,
                item.ProjectId,
                WorkItemChangeKinds.ItemUpdated,
                actor,
                now,
                new Dictionary<string, object?> { ["epic_id"] = epicId, ["version"] = item.Version },
                new Dictionary<string, object?> { ["epic_id"] = null, ["version"] = item.Version + 1 }))
            .ToList();
    }

    /// <summary>The order that puts an item at the end of a column.</summary>
    public static async Task<int> NextOrderAsync(KanbanDbContext dbContext, Guid projectId, WorkItemStatus status, CancellationToken cancellationToken)
    {
        var maxOrder = await dbContext.WorkItems
            .Where(item => item.ProjectId == projectId && item.Status == status)
            .Select(item => (int?)item.Order)
            .MaxAsync(cancellationToken);

        return maxOrder.GetValueOrDefault(-1) + 1;
    }

    /// <summary>
    /// Turns labels into the stored form: trimmed, without duplicates (ignoring case), joined with commas; null when
    /// there are none. The labels come either as one comma-separated string or as a list, never both. A label of a
    /// list may not hold a comma, and all labels together are at most <see cref="WorkItemLimits.MaxLabelsLength"/>
    /// characters. Messages name the arguments as the MCP tools spell them.
    /// </summary>
    public static bool TryNormalizeLabels(string? commaSeparated, IReadOnlyList<string>? list, out string? stored, out string error)
    {
        stored = null;
        error = string.Empty;
        if (commaSeparated is not null && list is not null)
        {
            error = "Pass the labels either as 'labels' or as 'label_list', not both.";
            return false;
        }

        IEnumerable<string> labels;
        if (list is not null)
        {
            if (list.Any(label => string.IsNullOrWhiteSpace(label) || label.Contains(',')))
            {
                error = "Every label of 'label_list' must be a non-empty string without a comma.";
                return false;
            }

            labels = list.Select(label => label.Trim());
        }
        else
        {
            labels = (commaSeparated ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        var joined = string.Join(',', labels.Distinct(StringComparer.OrdinalIgnoreCase));
        if (joined.Length > WorkItemLimits.MaxLabelsLength)
        {
            error = $"The labels of a work item, joined with commas, must be at most {WorkItemLimits.MaxLabelsLength} characters.";
            return false;
        }

        stored = joined.Length == 0 ? null : joined;
        return true;
    }

    /// <summary>The stored labels as a list.</summary>
    public static IReadOnlyList<string> SplitLabels(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // The project of an item, or null when there is no such item. A project id never changes, so it can be read
    // before the write's transaction starts.
    public static async Task<Guid?> ProjectOfAsync(KanbanDbContext dbContext, Guid itemId, CancellationToken cancellationToken) =>
        await dbContext.WorkItems
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => (Guid?)item.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Holds the row of the project until the transaction ends, as its first statement: the writes that give an item
    /// a place in a column of the project or take one out of it (a create, an import, a move, an update that names a
    /// status, a delete of an item or of the project) then run one at a time. On MariaDB (REPEATABLE READ) the reads that follow see everything the earlier holders committed;
    /// a read before it would fix the transaction's view too early. A project that was deleted in between holds
    /// nothing, and the write then finds no item.
    /// </summary>
    public static Task<int> HoldColumnsAsync(KanbanDbContext dbContext, Guid projectId, CancellationToken cancellationToken) =>
        Projects.LockAsync(dbContext, projectId, cancellationToken);

    // One UPDATE that advances the version together with whatever else the write sets, and only when the item still
    // has the expected version. Returns null when it wrote, and the refusal when it did not.
    private static async Task<WorkItemWriteResult?> AdvanceAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        long? expectedVersion,
        Action<UpdateSettersBuilder<WorkItem>> set,
        CancellationToken cancellationToken)
    {
        var target = dbContext.WorkItems.Where(item => item.Id == itemId);
        if (expectedVersion is { } expected)
        {
            target = target.Where(item => item.Version == expected);
        }

        var updated = await target.ExecuteUpdateAsync(
            setters =>
            {
                setters.SetProperty(item => item.Version, item => item.Version + 1);
                set(setters);
            },
            cancellationToken);
        if (updated > 0)
        {
            return null;
        }

        var current = await dbContext.WorkItems
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => new { item.Version })
            .FirstOrDefaultAsync(cancellationToken);
        return current is null
            ? new(WorkItemWriteOutcome.NotFound)
            : new(WorkItemWriteOutcome.VersionConflict, Message:
                $"Work item {itemId} is at version {current.Version}, not {expectedVersion}: it was changed after it was read. Nothing was changed; read it again and repeat the call.");
    }

    private static async Task NormalizeColumnOrderingAsync(
        KanbanDbContext dbContext,
        Guid projectId,
        WorkItemStatus status,
        Guid movedItemId,
        int desiredOrder,
        CancellationToken cancellationToken)
    {
        var items = await dbContext.WorkItems
            .Where(item => item.ProjectId == projectId && item.Status == status && item.Id != movedItemId)
            .OrderBy(item => item.Order)
            .ToListAsync(cancellationToken);

        // Keep column order deterministic before reassigning contiguous order values.
        items = items
            .OrderBy(item => item.Order)
            .ThenBy(item => item.CreatedAtUtc)
            .ToList();

        desiredOrder = Math.Clamp(desiredOrder, 0, items.Count);
        var index = 0;

        foreach (var item in items)
        {
            if (index == desiredOrder)
            {
                index++;
            }

            // Order only: a sibling that merely shifts has not changed, so its UpdatedAtUtc and version stay.
            item.Order = index++;
        }
    }

    private static WorkItemWriteResult Invalid(string message) => new(WorkItemWriteOutcome.Invalid, Message: message);
}
