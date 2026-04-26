namespace KanbanBoard.Shared.Contracts;

public static class KanbanInternalAuth
{
    public const string SecretHeaderName = "X-Kanban-Internal-Secret";
    public const string AppUserIdHeaderName = "X-Kanban-Internal-App-User-Id";
    public const string PersonalAccessTokenIdHeaderName = "X-Kanban-Internal-Pat-Id";
    public const string AuthenticationType = "KanbanInternal";
    public const string InternalRequestItemKey = "KanbanBoard.InternalRequest";
}

public sealed record ValidatePersonalAccessTokenRequest(string Token);

public sealed record ValidatedPersonalAccessTokenDto(
    Guid AppUserId,
    Guid PersonalAccessTokenId,
    string? DisplayName,
    string? Email,
    DateTimeOffset? ExpiresAtUtc);
