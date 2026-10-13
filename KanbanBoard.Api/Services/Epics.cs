using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

/// <summary>
/// The rules for a new epic and a new epic document, and their creation. Every way of creating one goes through
/// here: the create endpoints and the import of an epic plan. Everything here runs on SQLite in the tests and on
/// MariaDB through MySql.EntityFrameworkCore in production.
/// </summary>
public static class Epics
{
    // The unique indexes on the idempotency keys, as the server names them in error 1062.
    public const string IdempotencyIndexName = "IX_Epics_ProjectId_IdempotencyKeyHash";
    public const string DocumentIdempotencyIndexName = "IX_EpicDocuments_EpicId_IdempotencyKeyHash";

    /// <summary>A new epic with its fields as they are stored, or why they are refused.</summary>
    public static bool TryNew(Guid projectId, string? name, string? description, bool isArchived, out Epic epic, out string error)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        epic = new Epic
        {
            ProjectId = projectId,
            Name = normalizedName,
            Description = description?.Trim(),
            IsArchived = isArchived,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        error = normalizedName.Length == 0 ? "Argument 'name' is required."
            : normalizedName.Length > EpicLimits.MaxNameLength ? $"Argument 'name' must be at most {EpicLimits.MaxNameLength} characters."
            : string.Empty;
        return error.Length == 0;
    }

    /// <summary>A new document with its fields as they are stored (the body is kept as it is), or why they are refused.</summary>
    public static bool TryNewDocument(Guid epicId, string? title, string? body, out EpicDocument document, out string error)
    {
        var normalizedTitle = title?.Trim() ?? string.Empty;
        document = new EpicDocument
        {
            EpicId = epicId,
            Title = normalizedTitle,
            Body = body ?? string.Empty,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        error = normalizedTitle.Length == 0 ? "Argument 'title' is required."
            : normalizedTitle.Length > EpicLimits.MaxDocumentTitleLength ? $"Argument 'title' must be at most {EpicLimits.MaxDocumentTitleLength} characters."
            : string.IsNullOrWhiteSpace(body) ? "Argument 'body' is required."
            : string.Empty;
        return error.Length == 0;
    }

    /// <summary>
    /// Holds the epic's row until the transaction ends and changes nothing; returns 0 when there is no such epic.
    /// The archive and the delete of an epic take it after the project's row and before the rows of the epic's
    /// items. A write that puts an item into the epic reads this row (its foreign key) and then adds the item to
    /// the index on the epic; every such write (a create, an import, an update that names an epic) holds the
    /// project's row first as well, so it runs before the archive or after it, never across it.
    /// </summary>
    public static Task<int> LockAsync(KanbanDbContext dbContext, Guid epicId, CancellationToken cancellationToken) =>
        dbContext.Epics
            .Where(epic => epic.Id == epicId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(epic => epic.IsArchived, epic => epic.IsArchived), cancellationToken);

    /// <summary>
    /// Stores a new epic. With an <paramref name="idempotencyKeyHash"/> that already belongs to an epic of the
    /// project, nothing is written and that epic is reported, whatever its fields.
    /// </summary>
    public static async Task<KeyedCreateResult> CreateAsync(KanbanDbContext dbContext, Epic epic, string? idempotencyKeyHash, CancellationToken cancellationToken)
    {
        if (idempotencyKeyHash is null)
        {
            if (!await dbContext.Projects.AnyAsync(project => project.Id == epic.ProjectId, cancellationToken))
            {
                return new(KeyedCreateOutcome.NotFound);
            }

            dbContext.Epics.Add(epic);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(KeyedCreateOutcome.Created, epic.Id);
        }

        epic.IdempotencyKeyHash = idempotencyKeyHash;
        return await IdempotencyKeys.CreateOnceAsync(
            dbContext,
            () => Projects.LockAsync(dbContext, epic.ProjectId, cancellationToken),
            async () => (await dbContext.Epics
                .AsNoTracking()
                .Where(candidate => candidate.ProjectId == epic.ProjectId && candidate.IdempotencyKeyHash == idempotencyKeyHash)
                .Select(candidate => new { candidate.Id })
                .FirstOrDefaultAsync(cancellationToken))?.Id,
            async transaction =>
            {
                dbContext.Epics.Add(epic);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(KeyedCreateOutcome.Created, epic.Id);
            },
            IdempotencyIndexName,
            cancellationToken);
    }

    /// <summary>
    /// Stores a new document of an epic. With an <paramref name="idempotencyKeyHash"/> that already belongs to a
    /// document of the epic, nothing is written and that document is reported, whatever its fields.
    /// </summary>
    public static async Task<KeyedCreateResult> CreateDocumentAsync(KanbanDbContext dbContext, EpicDocument document, string? idempotencyKeyHash, CancellationToken cancellationToken)
    {
        if (idempotencyKeyHash is null)
        {
            if (!await dbContext.Epics.AnyAsync(epic => epic.Id == document.EpicId, cancellationToken))
            {
                return new(KeyedCreateOutcome.NotFound);
            }

            dbContext.EpicDocuments.Add(document);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(KeyedCreateOutcome.Created, document.Id);
        }

        document.IdempotencyKeyHash = idempotencyKeyHash;
        return await IdempotencyKeys.CreateOnceAsync(
            dbContext,
            // The epic's row, held without changing it.
            () => dbContext.Epics
                .Where(epic => epic.Id == document.EpicId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(epic => epic.IsArchived, epic => epic.IsArchived), cancellationToken),
            async () => (await dbContext.EpicDocuments
                .AsNoTracking()
                .Where(candidate => candidate.EpicId == document.EpicId && candidate.IdempotencyKeyHash == idempotencyKeyHash)
                .Select(candidate => new { candidate.Id })
                .FirstOrDefaultAsync(cancellationToken))?.Id,
            async transaction =>
            {
                dbContext.EpicDocuments.Add(document);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(KeyedCreateOutcome.Created, document.Id);
            },
            DocumentIdempotencyIndexName,
            cancellationToken);
    }
}
