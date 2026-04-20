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
    DateTimeOffset UpdatedAtUtc);

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
