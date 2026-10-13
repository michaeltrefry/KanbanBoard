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
    /// <summary>
    /// Writes the set fields of <paramref name="patch"/> to the item, or writes nothing and says why. With an
    /// expected version, the first statement is one conditional UPDATE, so the database decides between concurrent
    /// updates: only one of them can match the version. Do not replace it with a read followed by SaveChanges.
    /// </summary>
    public static async Task<WorkItemWriteResult> UpdateAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        UpdateWorkItemPatch patch,
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

        // The UPDATE comes first and holds the item's row until the transaction ends: the updates of one item run
        // one at a time, and each reads the item only after the one before it has committed.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
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

        // An epic is checked when the update names one: an update that leaves the epic alone is not refused
        // because that epic has been archived since.
        if (patch is { HasEpicId: true, EpicId: not null } &&
            !await dbContext.Epics.AnyAsync(epic => epic.Id == patch.EpicId && epic.ProjectId == item.ProjectId && !epic.IsArchived, cancellationToken))
        {
            // Leaving the transaction uncommitted takes the version and the stamp back.
            return Invalid("Epic must belong to the same project as the work item and be active.");
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
        await transaction.CommitAsync(cancellationToken);

        if (item.EpicId is null)
        {
            item.Epic = null;
        }
        else
        {
            await dbContext.Entry(item).Reference(workItem => workItem.Epic).LoadAsync(cancellationToken);
        }

        return new(WorkItemWriteOutcome.Succeeded, item);
    }

    /// <summary>
    /// Puts the item in a column at a position. The item itself is written by one conditional UPDATE; the siblings
    /// it displaces are renumbered afterwards, in a save of their own, and keep their version and UpdatedAtUtc.
    /// </summary>
    public static async Task<WorkItemWriteResult> MoveAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        WorkItemStatus status,
        int order,
        long? expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        WorkItem? moved;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
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
                return refusal;
            }

            // Read while the UPDATE still holds the row, so the item that is returned carries this move's version.
            moved = await dbContext.WorkItems
                .AsNoTracking()
                .Include(workItem => workItem.Epic)
                .Include(workItem => workItem.Comments)
                .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
            if (moved is null)
            {
                return new(WorkItemWriteOutcome.NotFound);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // The siblings are written after that transaction, without holding the moved item's row. Each save writes
        // its rows in the order of their keys, so two moves in one column wait for each other instead of deadlocking.
        await NormalizeColumnOrderingAsync(dbContext, moved.ProjectId, status, itemId, order, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(WorkItemWriteOutcome.Succeeded, moved);
    }

    /// <summary>
    /// Takes every unfinished item out of the epic, as one UPDATE that also advances their versions. Returns the
    /// number of items it took out.
    /// </summary>
    public static Task<int> DetachFromEpicAsync(
        KanbanDbContext dbContext,
        Guid epicId,
        bool unfinishedOnly,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var items = dbContext.WorkItems.Where(item => item.EpicId == epicId);
        if (unfinishedOnly)
        {
            items = items.Where(item => item.Status != WorkItemStatus.Done && item.Status != WorkItemStatus.Closed);
        }

        return items.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.EpicId, (Guid?)null)
                .SetProperty(item => item.UpdatedAtUtc, now)
                .SetProperty(item => item.Version, item => item.Version + 1),
            cancellationToken);
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
