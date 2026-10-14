using System.Text.Json;
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

/// <param name="RepositoryUrl">The repository the project's work is done in, or null when none is set.</param>
/// <param name="DefaultBranch">The branch of that repository work starts from, or null when none is set.</param>
public sealed record ProjectBoardDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    bool IsArchived,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<WorkItemDto> Items,
    // Always written, also as null, as on ProjectSummaryDto.
    [property: JsonPropertyName("repository_url"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RepositoryUrl = null,
    [property: JsonPropertyName("default_branch"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DefaultBranch = null);

public sealed record EpicDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    bool IsArchived,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>Only on the answer to creating an epic: false when the idempotency key was a repeat and nothing was created.</summary>
    [JsonPropertyName("created"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Created { get; init; }
}

public sealed record EpicDocumentDto(
    Guid Id,
    Guid EpicId,
    string Title,
    string Body,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>Only on the answer to creating an epic document: false when the idempotency key was a repeat and nothing was created.</summary>
    [JsonPropertyName("created"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Created { get; init; }
}

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
    // Live claim, or all null when the item is unclaimed or its claim has expired. Always written, also as null.
    [property: JsonPropertyName("claimant"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Claimant = null,
    [property: JsonPropertyName("claimant_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ClaimantId = null,
    [property: JsonPropertyName("claim_marker"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ClaimMarker = null,
    [property: JsonPropertyName("claim_expires_at_utc"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ClaimExpiresAtUtc = null)
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

    /// <summary>Only on the answer to creating a work item: false when the idempotency key was a repeat and nothing was created.</summary>
    [JsonPropertyName("created"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Created { get; init; }
}

/// <summary>
/// One page of a work item list. <see cref="NextCursor"/> is null on the last page; otherwise pass it back
/// unchanged, with the same filters, to get the next page.
/// </summary>
public sealed record WorkItemPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkItemDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>Who made a change: the user behind the token or the browser session that wrote it.</summary>
public sealed record WorkItemChangeActorDto(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>
/// One entry of the change feed: one change to one work item. Entries are never changed or removed, and they stay
/// after their item is deleted.
/// </summary>
/// <param name="Sequence">The position in the feed: every later entry has a higher one. Sequences may skip numbers.</param>
/// <param name="Kind">One of <see cref="WorkItemChangeKinds"/>.</param>
/// <param name="Actor">Null when the write carried neither a token nor a browser session.</param>
/// <param name="Changes">A JSON array of <c>{"field", "old", "new"}</c>, one per field the write changed.</param>
public sealed record WorkItemChangeDto(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("project_id")] Guid ProjectId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("actor")] WorkItemChangeActorDto? Actor,
    [property: JsonPropertyName("changes")] JsonElement Changes,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc);

/// <summary>
/// The entries of the change feed after a cursor, oldest first. Unlike the other pages, <see cref="NextCursor"/> is
/// always set: it is the position after the last entry returned, or the position that was asked for when there was
/// none, so a poller passes it to its next call whatever came back.
/// </summary>
public sealed record WorkItemChangePageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkItemChangeDto> Items,
    [property: JsonPropertyName("next_cursor")] string NextCursor);

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
    DateTimeOffset? LastSeenAtUtc,
    bool IsAdmin = false);

/// <param name="ActiveTokenCount">Tokens that are neither revoked nor expired.</param>
/// <param name="LastUsedAtUtc">The most recent use of any of its tokens; null when none was ever used.</param>
/// <param name="DisabledAtUtc">When the identity was disabled; null while it is enabled.</param>
public sealed record ServiceIdentitySummaryDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    int ActiveTokenCount,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? DisabledAtUtc = null)
{
    /// <summary>A disabled identity gets no new token and none of its tokens is accepted.</summary>
    public bool Disabled => DisabledAtUtc is not null;
}

public sealed record CreateServiceIdentityRequest(string? Name);

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

/// <summary>The states of a token in an admin's list, by precedence: a revoked token is revoked even once it expired.</summary>
public static class PersonalAccessTokenStates
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

/// <summary>A token of a service identity as the admin page lists it: never its plaintext or secret.</summary>
/// <param name="State">One of <see cref="PersonalAccessTokenStates"/>.</param>
public sealed record ServiceIdentityTokenDto(
    Guid Id,
    string Name,
    string Scope,
    string TokenPrefix,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string State);

/// <param name="ExpiresAtUtc">The replacement's expiry; null for none, whatever the rotated token's was.</param>
public sealed record RotatePersonalAccessTokenRequest(DateTimeOffset? ExpiresAtUtc = null);

/// <summary>The replacement of a rotated token, shown once, and the id of the token it replaced, now revoked.</summary>
public sealed record RotatedPersonalAccessTokenDto(
    PersonalAccessTokenMetadataDto Token,
    string PlaintextToken,
    Guid RevokedTokenId);

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

/// <param name="IdempotencyKey">Makes the create safe to repeat: a key the caller already used in the project returns that epic.</param>
public sealed record CreateEpicRequest(
    string Name,
    string? Description,
    bool IsArchived = false,
    string? IdempotencyKey = null);

public sealed record UpdateEpicRequest(
    string Name,
    string? Description,
    bool IsArchived = false);

/// <param name="IdempotencyKey">Makes the create safe to repeat: a key the caller already used on the epic returns that document.</param>
public sealed record CreateEpicDocumentRequest(
    string Title,
    string Body,
    string? IdempotencyKey = null);

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
    IReadOnlyList<string>? LabelList = null,
    // Makes the create safe to repeat: a key the caller already used in the project returns that item.
    string? IdempotencyKey = null);

/// <summary>The epic of a plan given to <c>import_epic_plan</c>.</summary>
public sealed record ImportEpicPlanEpic(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null)
{
    /// <summary>The fields the epic of a plan does not have. A plan that carries one is refused: a misspelt field is not silently dropped.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>A document of a plan: it becomes a document of the epic.</summary>
public sealed record ImportEpicPlanDocument(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>
/// A work item of a plan. <see cref="Ref"/> is the caller's own name for it, unique within the plan: the other items
/// name it in <see cref="BlockedBy"/>, and the answer maps it to the id of the item that was created. The type, the
/// status and the priority are the names of <see cref="WorkItemType"/>, <see cref="WorkItemStatus"/> and
/// <see cref="WorkItemPriority"/>; left out they are Story, Backlog and Medium.
/// </summary>
public sealed record ImportEpicPlanItem(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("priority")] string? Priority = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("estimate")] int? Estimate = null,
    [property: JsonPropertyName("labels")] string? Labels = null,
    [property: JsonPropertyName("label_list")] IReadOnlyList<string>? LabelList = null,
    [property: JsonPropertyName("blocked_by")] IReadOnlyList<string>? BlockedBy = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>The body of <c>POST /api/projects/{projectId}/epic-plans</c>: an epic, its documents, its items and their dependencies.</summary>
public sealed record ImportEpicPlanRequest(
    [property: JsonPropertyName("epic")] ImportEpicPlanEpic Epic,
    [property: JsonPropertyName("items")] IReadOnlyList<ImportEpicPlanItem> Items,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("documents")] IReadOnlyList<ImportEpicPlanDocument>? Documents = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>
/// What an import created, or what the first import with the same idempotency key created.
/// </summary>
/// <param name="Created">False when the idempotency key was a repeat: nothing was created and the ids are those of the first import.</param>
/// <param name="DocumentIds">The documents, in the order of the plan.</param>
/// <param name="ItemIds">The id of the work item created for each <c>ref</c> of the plan.</param>
public sealed record ImportEpicPlanResultDto(
    [property: JsonPropertyName("created")] bool Created,
    [property: JsonPropertyName("project_id")] Guid ProjectId,
    [property: JsonPropertyName("epic_id")] Guid EpicId,
    [property: JsonPropertyName("document_ids")] IReadOnlyList<Guid> DocumentIds,
    [property: JsonPropertyName("item_ids")] IReadOnlyDictionary<string, Guid> ItemIds);

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
