using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Api.Models;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsArchived { get; set; }
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
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<EpicDocument> Documents { get; set; } = [];
    public List<WorkItem> Items { get; set; } = [];
}

public sealed class EpicDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EpicId { get; set; }
    public Epic? Epic { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
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
    public string? Labels { get; set; }
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
    // WorkItemComments.HashIdempotencyKey of the caller's idempotency key, or null when none was passed.
    public string? IdempotencyKeyHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
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
}
