using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Api.Models;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsArchived { get; set; }
    // Where the project's work is done: an http, https or ssh repository URL and a branch of it. Both optional.
    public string? RepositoryUrl { get; set; }
    public string? DefaultBranch { get; set; }
    // IdempotencyKeys.Hash of the idempotency key the project was created with, or null when none was passed.
    public string? IdempotencyKeyHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<Epic> Epics { get; set; } = [];
    public List<WorkItem> Items { get; set; } = [];
}

public sealed class Epic
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsArchived { get; set; }
    // IdempotencyKeys.Hash of the idempotency key the epic was created with, or null when none was passed.
    public string? IdempotencyKeyHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<EpicDocument> Documents { get; set; } = [];
    public List<WorkItem> Items { get; set; } = [];
}

// What one import of an epic plan created, kept so that a repeat of its idempotency key answers with the same ids.
// Written only by EpicPlans, in the transaction of the import, and never updated: it goes with its epic.
public sealed class EpicPlanImport
{
    public Guid EpicId { get; set; }
    public Epic? Epic { get; set; }
    public Guid ProjectId { get; set; }
    // IdempotencyKeys.Hash of the idempotency key of the import.
    public string IdempotencyKeyHash { get; set; } = string.Empty;
    // JSON: {"document_ids": [...], "item_ids": {"<ref>": "<id>", ...}}, as the import answered.
    public string Result { get; set; } = "{}";
}

public sealed class EpicDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EpicId { get; set; }
    public Epic? Epic { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    // IdempotencyKeys.Hash of the idempotency key the document was created with, or null when none was passed.
    public string? IdempotencyKeyHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? EpicId { get; set; }
    public Epic? Epic { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public WorkItemType Type { get; set; }
    public WorkItemStatus Status { get; set; } = WorkItemStatus.Backlog;
    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;
    public int Order { get; set; }
    public int? Estimate { get; set; }
    // Comma-separated, without spaces around the commas (WorkItemWrites.TryNormalizeLabels).
    public string? Labels { get; set; }
    // Counts the writes to the item's own fields. Written only by WorkItemWrites, as Version + 1 inside the UPDATE.
    public long Version { get; set; } = 1;
    // IdempotencyKeys.Hash of the idempotency key the item was created with, or null when none was passed.
    public string? IdempotencyKeyHash { get; set; }
    public List<WorkItemComment> Comments { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    // Claim: all four are set together by WorkItemClaims and cleared together on release.
    public Guid? ClaimantUserId { get; set; }
    public string? ClaimantName { get; set; }
    public string? ClaimMarker { get; set; }
    public DateTimeOffset? ClaimExpiresAtUtc { get; set; }
}

// One edge: BlockedItem cannot start until BlockerItem is Done. Written only by WorkItemDependencies.
public sealed class WorkItemDependency
{
    public Guid BlockedItemId { get; set; }
    public WorkItem? BlockedItem { get; set; }
    public Guid BlockerItemId { get; set; }
    public WorkItem? BlockerItem { get; set; }
}

// An external URL (a pull request, a branch, ...) attached to a work item. Written only by WorkItemLinks.
public sealed class WorkItemLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public WorkItem? WorkItem { get; set; }
    // One of WorkItemLinkKinds.
    public string Kind { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    // SHA-256 of Url in lowercase hex. Lookups and uniqueness use it instead of Url: it fits an index key whatever
    // the length of the URL, and it matches exactly where the database's default collation would ignore case.
    public string UrlHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkItemComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public WorkItem? WorkItem { get; set; }
    public string Author { get; set; } = string.Empty;
    // The user behind the token that wrote the comment. Null for a comment written without a token (the web UI)
    // and for every comment older than this column.
    public Guid? AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    // IdempotencyKeys.Hash of the caller's idempotency key, or null when none was passed.
    public string? IdempotencyKeyHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

// One change to a work item, appended in the transaction of the write it describes. Written only by
// WorkItemChangeLog and never updated or deleted: it has no foreign key, so it outlives its item and its project.
public sealed class WorkItemChange
{
    // The position in the feed. The database assigns it while the writer holds the change log lock, so the entries
    // are committed in the order of their sequences (WorkItemChangeLog.CommitAsync).
    public long Sequence { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid ProjectId { get; set; }
    // One of WorkItemChangeKinds.
    public string Kind { get; set; } = string.Empty;
    // Who made the change: the user behind the token or the browser session. Both null when the request had neither.
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    // A JSON array of {"field", "old", "new"}.
    public string Changes { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    /// <summary>
    /// When a service identity was disabled; null while it is enabled, and always for a person. A disabled identity
    /// gets no new token and none of its tokens is accepted. It is never deleted: comments, claims and change-feed
    /// entries refer to it by id.
    /// </summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }
    public List<PersonalAccessToken> PersonalAccessTokens { get; set; } = [];
}

public sealed class PersonalAccessToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string EncryptedSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = PersonalAccessTokenScopes.Full;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    // The admin who issued the token from the Admin page (by an issue or a rotate), and the admin who revoked it there
    // (by a revoke, a rotate or a disable of its identity): the user's id and name at that moment, as a work item
    // change keeps its actor. No foreign key, so the record outlives a change of the user. All null for a token
    // issued or revoked otherwise: by its owner on the Settings page, by create-service-identity, or before 012.
    public Guid? IssuedByUserId { get; set; }
    public string? IssuedByName { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokedByName { get; set; }
}
