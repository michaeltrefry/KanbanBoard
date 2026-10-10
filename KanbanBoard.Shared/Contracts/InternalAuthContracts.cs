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
    DateTimeOffset? ExpiresAtUtc,
    string Scope = PersonalAccessTokenScopes.Full);

public static class PersonalAccessTokenScopes
{
    public const string Read = "read";
    public const string Full = "full";

    public static bool IsValid(string? scope) =>
        string.Equals(scope, Read, StringComparison.Ordinal) ||
        string.Equals(scope, Full, StringComparison.Ordinal);

    // A missing scope means the token or payload predates scopes and keeps full access.
    // Any other value than "full" (including "read" and unknown values) is denied writes.
    public static bool AllowsWrites(string? scope) =>
        string.IsNullOrEmpty(scope) || string.Equals(scope, Full, StringComparison.Ordinal);
}
