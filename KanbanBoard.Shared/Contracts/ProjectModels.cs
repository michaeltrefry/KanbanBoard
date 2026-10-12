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

public sealed record ProjectSummaryDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    bool IsArchived,
    DateTimeOffset CreatedAtUtc,
    int TotalItems,
    int OpenItems);

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

public sealed record WorkItemCommentDto(
    Guid Id,
    Guid WorkItemId,
    string Author,
    string Body,
    DateTimeOffset CreatedAtUtc);

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
    int? Estimate,
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
}

public sealed record WorkItemDependencyRefDto(
    Guid Id,
    string Title,
    WorkItemStatus Status);

public sealed record WorkItemSearchResultDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    WorkItemType Type,
    WorkItemStatus Status);

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

public sealed record CreateProjectRequest(
    string Name,
    string Key,
    string? Description,
    bool IsArchived = false);

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
    string? Labels);

public sealed record UpdateWorkItemRequest(
    Guid? EpicId,
    string Title,
    string? Description,
    WorkItemType Type,
    WorkItemStatus Status,
    WorkItemPriority Priority,
    int? Estimate,
    string? Labels);

public sealed record MoveWorkItemRequest(
    WorkItemStatus Status,
    int Order);

public sealed record CreateWorkItemCommentRequest(
    string Author,
    string Body);

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
