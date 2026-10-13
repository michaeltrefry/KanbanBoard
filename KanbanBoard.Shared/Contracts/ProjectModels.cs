using System.Text.Json.Serialization;

namespace KanbanBoard.Shared.Contracts;

public enum WorkItemType
{
    Story,
    Issue,
    Task
}

public enum WorkItemStatus
{
    Backlog,
    Ready,
    InProgress,
    Blocked,
    Done,
    Closed
}

public enum WorkItemPriority
{
    Low,
    Medium,
    High,
    Critical
}

/// <param name="RepositoryUrl">The repository the project's work is done in, or null when none is set.</param>
/// <param name="DefaultBranch">The branch of that repository work starts from, or null when none is set.</param>
public sealed record ProjectSummaryDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    bool IsArchived,
    DateTimeOffset CreatedAtUtc,
    int TotalItems,
    int OpenItems,
    // Always written, also as null: the MCP server leaves null fields out unless told otherwise.
    [property: JsonPropertyName("repository_url"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RepositoryUrl = null,
    [property: JsonPropertyName("default_branch"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DefaultBranch = null)
{
    /// <summary>Only on the answer to creating a project: false when the idempotency key was a repeat and nothing was created.</summary>
    [JsonPropertyName("created"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Created { get; init; }
}

public sealed record ProjectBoardDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    bool IsArchived,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<WorkItemDto> Items);

public sealed record EpicDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    bool IsArchived,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record EpicDocumentDto(
    Guid Id,
    Guid EpicId,
    string Title,
    string Body,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record EpicReferenceDto(
    Guid Id,
    string Name);

/// <param name="AuthorId">
/// Id of the identity whose token wrote the comment; <paramref name="Author"/> is then that identity's name at the
/// time. Left out when the comment was written without a token, where the author is the name the writer typed.
/// </param>
public sealed record WorkItemCommentDto(
    Guid Id,
    Guid WorkItemId,
    string Author,
    string Body,
    DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("author_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AuthorId = null);

/// <summary>
/// One page of a work item's comments, oldest first. <see cref="NextCursor"/> is null on the last page; otherwise
/// pass it back unchanged to get the next page.
/// </summary>
public sealed record WorkItemCommentPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkItemCommentDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record WorkItemDto(
    Guid Id,
    Guid ProjectId,
    Guid? EpicId,
    EpicReferenceDto? Epic,
    string Title,
    string? Description,
    WorkItemType Type,
    WorkItemStatus Status,
    WorkItemPriority Priority,
    int Order,
    // Always written, also as null: the MCP server leaves null fields out unless told otherwise.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Estimate,
    // The labels as one comma-separated string, as it has always been; LabelList is the same labels as a list.
    string? Labels,
    IReadOnlyList<WorkItemCommentDto> Comments,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    // Live claim, or all null when the item is unclaimed or its claim has expired.
    [property: JsonPropertyName("claimant")] string? Claimant = null,
    [property: JsonPropertyName("claimant_id")] Guid? ClaimantId = null,
    [property: JsonPropertyName("claim_marker")] string? ClaimMarker = null,
    [property: JsonPropertyName("claim_expires_at_utc")] DateTimeOffset? ClaimExpiresAtUtc = null)
{
    /// <summary>Work items this item is blocked by, each with its current status.</summary>
    [JsonPropertyName("blockers")]
    public IReadOnlyList<WorkItemDependencyRefDto> Blockers { get; init; } = [];

    /// <summary>Work items that are blocked by this item, each with its current status.</summary>
    [JsonPropertyName("dependents")]
    public IReadOnlyList<WorkItemDependencyRefDto> Dependents { get; init; } = [];

    /// <summary>External links attached to this item, oldest first.</summary>
    [JsonPropertyName("links")]
    public IReadOnlyList<WorkItemLinkDto> Links { get; init; } = [];

    /// <summary>
    /// Counts the writes to the item's own fields: 1 when the item is created, one more for every update and every
    /// move of the item. Pass it back as the expected version of an update or a move to refuse the write when the
    /// item was changed in between.
    /// </summary>
    [JsonPropertyName("version")]
    public long Version { get; init; }

    /// <summary>The labels of <see cref="Labels"/> as a list, in the same order; empty when the item has none.</summary>
    [JsonPropertyName("label_list")]
    public IReadOnlyList<string> LabelList { get; init; } = [];

    /// <summary>
    /// Only on the answer to adding a comment: the id of the comment that was added, or of the earlier comment the
    /// idempotency key already belonged to. It is one of <see cref="Comments"/>.
    /// </summary>
    [JsonPropertyName("comment_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CommentId { get; init; }

    /// <summary>Only on the answer to adding a comment: false when the idempotency key was a repeat and nothing was added.</summary>
    [JsonPropertyName("comment_created"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CommentCreated { get; init; }
}

/// <summary>
/// One page of a work item list. <see cref="NextCursor"/> is null on the last page; otherwise pass it back
/// unchanged, with the same filters, to get the next page.
/// </summary>
public sealed record WorkItemPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkItemDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record WorkItemDependencyRefDto(
    Guid Id,
    string Title,
    WorkItemStatus Status);

/// <summary>An external URL attached to a work item. <see cref="Kind"/> is one of <see cref="WorkItemLinkKinds"/>.</summary>
public sealed record WorkItemLinkDto(
    Guid Id,
    string Kind,
    string Url,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc);

public sealed record WorkItemSearchResultDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    WorkItemType Type,
    WorkItemStatus Status,
    // Always written, also as null.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Estimate = null,
    [property: JsonPropertyName("version")] long Version = 0);

public sealed record CurrentUserDto(
    Guid Id,
    string Issuer,
    string Subject,
    string? DisplayName,
    string? Email,
    DateTimeOffset? LastSeenAtUtc);

public sealed record PersonalAccessTokenMetadataDto(
    Guid Id,
    string Name,
    string TokenPrefix,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    bool IsActive,
    string Scope = PersonalAccessTokenScopes.Full);

public sealed record CreatedPersonalAccessTokenDto(
    PersonalAccessTokenMetadataDto Token,
    string PlaintextToken);

/// <param name="IdempotencyKey">Makes the create safe to repeat: a key the caller already used returns that project.</param>
public sealed record CreateProjectRequest(
    string Name,
    string Key,
    string? Description,
    bool IsArchived = false,
    string? RepositoryUrl = null,
    string? DefaultBranch = null,
    string? IdempotencyKey = null);

public sealed record UpdateProjectRequest(
    string Name,
    string Key,
    string? Description,
    bool IsArchived = false);

public sealed record CreateEpicRequest(
    string Name,
    string? Description,
    bool IsArchived = false);

public sealed record UpdateEpicRequest(
    string Name,
    string? Description,
    bool IsArchived = false);

public sealed record CreateEpicDocumentRequest(
    string Title,
    string Body);

public sealed record UpdateEpicDocumentRequest(
    string Title,
    string Body);

public sealed record CreateWorkItemRequest(
    Guid ProjectId,
    Guid? EpicId,
    string Title,
    string? Description,
    WorkItemType Type,
    WorkItemStatus Status,
    WorkItemPriority Priority,
    int? Estimate,
    string? Labels,
    // The labels as a list, in place of the comma-separated Labels; passing both is refused.
    IReadOnlyList<string>? LabelList = null);

/// <param name="ExpectedVersion">When set, the move is refused unless the item still has this version.</param>
public sealed record MoveWorkItemRequest(
    WorkItemStatus Status,
    int Order,
    long? ExpectedVersion = null);

/// <param name="Author">Used only when the request carries no token identity; a token's comment is its identity's.</param>
public sealed record CreateWorkItemCommentRequest(
    string? Author,
    string Body,
    string? IdempotencyKey = null);

public sealed record CreatePersonalAccessTokenRequest(
    string Name,
    DateTimeOffset? ExpiresAtUtc,
    string? Scope = null);

public sealed record ClaimWorkItemRequest(
    string Marker,
    int TtlSeconds);

public sealed record ReleaseWorkItemClaimRequest(
    string Marker);

public sealed record AddWorkItemDependencyRequest(
    Guid BlockedByItemId);

public sealed record AddWorkItemLinkRequest(
    string Kind,
    string Url);
