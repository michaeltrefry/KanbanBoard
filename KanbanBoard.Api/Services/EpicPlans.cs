using System.Text.Json;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

/// <summary>A plan that was read and found acceptable: the rows an import of it stores, not yet stored.</summary>
/// <param name="Items">The items in the order of the plan, each with the caller's <c>ref</c> for it.</param>
/// <param name="Dependencies">The edges, in the order of the plan's items and of each item's <c>blocked_by</c>.</param>
public sealed record EpicPlan(
    Epic Epic,
    IReadOnlyList<EpicDocument> Documents,
    IReadOnlyList<(string Ref, WorkItem Item)> Items,
    IReadOnlyList<WorkItemDependency> Dependencies);

/// <param name="Plan">The plan, or null when it is refused.</param>
/// <param name="Code">The <see cref="KanbanErrorCodes"/> code of the refusal.</param>
public sealed record EpicPlanReadResult(EpicPlan? Plan, string? Code = null, string? Message = null);

/// <summary>
/// The import of an epic plan: an epic, its documents, its work items and their dependencies, stored together or not
/// at all. The plan is checked for structure only; what it says is the caller's business. Everything here runs on
/// SQLite in the tests and on MariaDB through MySql.EntityFrameworkCore in production.
/// </summary>
public static class EpicPlans
{
    // The unique index on the idempotency key, as the server names it in error 1062.
    public const string IdempotencyIndexName = "IX_EpicPlanImports_ProjectId_IdempotencyKeyHash";

    private sealed record StoredResult(IReadOnlyList<Guid> DocumentIds, IReadOnlyDictionary<string, Guid> ItemIds);

    /// <summary>
    /// Turns a plan into the rows an import stores, or says why the plan is refused. Nothing is read from the
    /// database: every item of a plan is new, so its dependencies can only name each other. The epic, the documents
    /// and the items go through the same rules as the tools that create one of them (<see cref="Epics.TryNew"/>,
    /// <see cref="Epics.TryNewDocument"/>, <see cref="WorkItemWrites.TryNew"/>), and a cycle is what
    /// <see cref="WorkItemDependencies.IsBlockedByAsync{TItem}"/> says it is. Messages name the fields as the plan
    /// spells them.
    /// </summary>
    public static async Task<EpicPlanReadResult> ReadAsync(Guid projectId, ImportEpicPlanRequest request, DateTimeOffset now)
    {
        static EpicPlanReadResult Invalid(string message) => new(null, KanbanErrorCodes.InvalidArgument, message);

        // The precision MariaDB stores. Each document and item is a microsecond later than the one before it, so
        // that the tools that list them oldest first return them in the order of the plan.
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));

        if (Unknown(request.Unknown, "epic, documents, items and idempotency_key") is { } unknownArgument)
        {
            return Invalid($"The plan has no field {unknownArgument}");
        }

        if (request.Epic is null)
        {
            return Invalid("Argument 'epic' is required.");
        }

        if (Unknown(request.Epic.Unknown, "name and description") is { } unknownEpicField)
        {
            return Invalid($"The epic of the plan has no field {unknownEpicField}");
        }

        if (!Epics.TryNew(projectId, request.Epic.Name, request.Epic.Description, isArchived: false, out var epic, out var epicError))
        {
            return Invalid($"Epic: {epicError}");
        }

        epic.CreatedAtUtc = epic.UpdatedAtUtc = now;

        var plannedDocuments = request.Documents ?? [];
        if (plannedDocuments.Count > EpicPlanLimits.MaxDocuments)
        {
            return Invalid($"A plan holds at most {EpicPlanLimits.MaxDocuments} documents; this one holds {plannedDocuments.Count}.");
        }

        var documents = new List<EpicDocument>();
        foreach (var planned in plannedDocuments)
        {
            var where = $"documents[{documents.Count}]";
            if (planned is null)
            {
                return Invalid($"{where} is null.");
            }

            if (Unknown(planned.Unknown, "title and body") is { } unknownDocumentField)
            {
                return Invalid($"{where} has no field {unknownDocumentField}");
            }

            if (!Epics.TryNewDocument(epic.Id, planned.Title, planned.Body, out var document, out var documentError))
            {
                return Invalid($"{where}: {documentError}");
            }

            document.CreatedAtUtc = document.UpdatedAtUtc = now.AddTicks(documents.Count * TimeSpan.TicksPerMicrosecond);
            documents.Add(document);
        }

        var plannedItems = request.Items ?? [];
        if (plannedItems.Count is 0 or > EpicPlanLimits.MaxItems)
        {
            return Invalid($"Argument 'items' must hold 1 to {EpicPlanLimits.MaxItems} items; it holds {plannedItems.Count}.");
        }

        var items = new List<(string Ref, WorkItem Item)>();
        var byRef = new Dictionary<string, WorkItem>(StringComparer.Ordinal);
        foreach (var planned in plannedItems)
        {
            var where = $"items[{items.Count}]";
            if (planned is null)
            {
                return Invalid($"{where} is null.");
            }

            var reference = planned.Ref;
            if (string.IsNullOrWhiteSpace(reference) || reference.Length > EpicPlanLimits.MaxRefLength || reference.Any(char.IsControl))
            {
                return Invalid($"{where}: Field 'ref' is required and must be at most {EpicPlanLimits.MaxRefLength} characters, without control characters.");
            }

            where = $"Item '{reference}' ({where})";
            if (Unknown(planned.Unknown, "ref, title, type, status, priority, description, estimate, labels, label_list and blocked_by") is { } unknownItemField)
            {
                return Invalid($"{where} has no field {unknownItemField}");
            }

            if (!TryParse(planned.Type, WorkItemType.Story, out var type))
            {
                return Invalid($"{where}: Field 'type' must be one of {string.Join(", ", Enum.GetNames<WorkItemType>())}.");
            }

            if (!TryParse(planned.Status, WorkItemStatus.Backlog, out var status))
            {
                return Invalid($"{where}: Field 'status' must be one of {string.Join(", ", Enum.GetNames<WorkItemStatus>())}.");
            }

            if (!TryParse(planned.Priority, WorkItemPriority.Medium, out var priority))
            {
                return Invalid($"{where}: Field 'priority' must be one of {string.Join(", ", Enum.GetNames<WorkItemPriority>())}.");
            }

            if (!WorkItemWrites.TryNew(
                    projectId, epic.Id, planned.Title, planned.Description, type, status, priority, planned.Estimate,
                    planned.Labels, planned.LabelList, out var item, out var itemError))
            {
                return Invalid($"{where}: {itemError}");
            }

            if (!byRef.TryAdd(reference, item))
            {
                return Invalid($"{where}: ref '{reference}' is used by more than one item of the plan. Refs are compared exactly.");
            }

            item.CreatedAtUtc = item.UpdatedAtUtc = now.AddTicks(items.Count * TimeSpan.TicksPerMicrosecond);
            items.Add((reference, item));
        }

        var dependencyCount = plannedItems.Sum(planned => planned.BlockedBy?.Count ?? 0);
        if (dependencyCount > EpicPlanLimits.MaxDependencies)
        {
            return Invalid($"A plan holds at most {EpicPlanLimits.MaxDependencies} dependencies (blocked_by entries); this one holds {dependencyCount}.");
        }

        // The edges are added to the plan's own graph one at a time, each checked as add_work_item_dependency checks
        // an edge against the stored ones.
        var blockersOf = items.ToDictionary(planned => planned.Item.Id, _ => new HashSet<Guid>());
        var dependencies = new List<WorkItemDependency>();
        foreach (var (planned, index) in plannedItems.Select((planned, index) => (planned, index)))
        {
            var blocked = items[index].Item;
            foreach (var blockerRef in planned.BlockedBy ?? [])
            {
                if (blockerRef is null || !byRef.TryGetValue(blockerRef, out var blocker))
                {
                    return Invalid($"Item '{planned.Ref}' (items[{index}]): blocked_by names '{blockerRef}', which is not the ref of an item of the plan.");
                }

                if (blocker.Id == blocked.Id ||
                    await WorkItemDependencies.IsBlockedByAsync(blocker.Id, blocked.Id, current => ValueTask.FromResult<IReadOnlyCollection<Guid>>(blockersOf[current])))
                {
                    return new(null, KanbanErrorCodes.Cycle,
                        $"Item '{planned.Ref}' blocked by '{blockerRef}' would make an item of the plan block itself, directly or through other items. Nothing was created.");
                }

                // Naming a blocker twice is one dependency, as adding an existing dependency changes nothing.
                if (blockersOf[blocked.Id].Add(blocker.Id))
                {
                    dependencies.Add(new WorkItemDependency { BlockedItemId = blocked.Id, BlockerItemId = blocker.Id });
                }
            }
        }

        return new(new EpicPlan(epic, documents, items, dependencies));
    }

    /// <summary>
    /// Stores the plan in one transaction, with one <c>item_created</c> entry of the change feed per item and then
    /// one <c>dependency_added</c> per dependency, committed with it. With an <paramref name="idempotencyKeyHash"/>
    /// that already belongs to an import into the project, nothing is written and the epic of that import is reported.
    ///
    /// The imports into one project run one at a time (<see cref="IdempotencyKeys.CreateOnceAsync"/>), so each reads
    /// the end of every column after the one before it has committed, and the change log lock is taken last, for the
    /// entries and the commit only.
    /// </summary>
    public static Task<KeyedCreateResult> ImportAsync(
        KanbanDbContext dbContext,
        EpicPlan plan,
        string idempotencyKeyHash,
        ChangeActor? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var projectId = plan.Epic.ProjectId;
        return IdempotencyKeys.CreateOnceAsync(
            dbContext,
            () => Projects.LockAsync(dbContext, projectId, cancellationToken),
            async () => (await dbContext.EpicPlanImports
                .AsNoTracking()
                .Where(import => import.ProjectId == projectId && import.IdempotencyKeyHash == idempotencyKeyHash)
                .Select(import => new { import.EpicId })
                .FirstOrDefaultAsync(cancellationToken))?.EpicId,
            async transaction =>
            {
                // The items go to the end of their columns, in the order of the plan.
                foreach (var column in plan.Items.GroupBy(planned => planned.Item.Status))
                {
                    var order = await WorkItemWrites.NextOrderAsync(dbContext, projectId, column.Key, cancellationToken);
                    foreach (var (_, item) in column)
                    {
                        item.Order = order++;
                    }
                }

                dbContext.Epics.Add(plan.Epic);
                dbContext.EpicDocuments.AddRange(plan.Documents);
                dbContext.WorkItems.AddRange(plan.Items.Select(planned => planned.Item));
                dbContext.WorkItemDependencies.AddRange(plan.Dependencies);
                dbContext.EpicPlanImports.Add(new EpicPlanImport
                {
                    EpicId = plan.Epic.Id,
                    ProjectId = projectId,
                    IdempotencyKeyHash = idempotencyKeyHash,
                    Result = JsonSerializer.Serialize(new StoredResult(
                        plan.Documents.Select(document => document.Id).ToList(),
                        plan.Items.ToDictionary(planned => planned.Ref, planned => planned.Item.Id)))
                });
                await dbContext.SaveChangesAsync(cancellationToken);

                // The items before the dependencies that name them.
                var entries = plan.Items
                    .Select(planned => WorkItemChangeLog.Entry(
                        planned.Item.Id, projectId, WorkItemChangeKinds.ItemCreated, actor, now, null, WorkItemChangeLog.Snapshot(planned.Item)))
                    .Concat(plan.Dependencies.Select(edge => WorkItemChangeLog.Entry(
                        edge.BlockedItemId, projectId, WorkItemChangeKinds.DependencyAdded, actor, now, null,
                        new Dictionary<string, object?> { ["blocked_by"] = edge.BlockerItemId })))
                    .ToList();
                await WorkItemChangeLog.CommitAsync(dbContext, transaction, entries, cancellationToken);
                return new(KeyedCreateOutcome.Created, plan.Epic.Id);
            },
            IdempotencyIndexName,
            cancellationToken);
    }

    /// <summary>
    /// What the import that created <paramref name="epicId"/> answered: the ids as they were created, whatever has
    /// happened to the objects since. Null when there is no such import (any more).
    /// </summary>
    public static async Task<ImportEpicPlanResultDto?> ResultAsync(KanbanDbContext dbContext, Guid epicId, bool created, CancellationToken cancellationToken)
    {
        var import = await dbContext.EpicPlanImports.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.EpicId == epicId, cancellationToken);
        if (import is null)
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredResult>(import.Result)!;
        return new(created, import.ProjectId, epicId, stored.DocumentIds, stored.ItemIds);
    }

    // "'x'. Its fields are ..." for the first field the plan carries that does not exist, or null.
    private static string? Unknown(Dictionary<string, JsonElement>? unknown, string fields) =>
        unknown is { Count: > 0 } ? $"'{unknown.Keys.First()}'. Its fields are {fields}." : null;

    // A name of the enum, ignoring case; a number is not a name.
    private static bool TryParse<TEnum>(string? value, TEnum fallback, out TEnum parsed) where TEnum : struct, Enum
    {
        parsed = fallback;
        return value is null ||
            (!value.Any(char.IsAsciiDigit) && Enum.TryParse(value.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed));
    }
}
