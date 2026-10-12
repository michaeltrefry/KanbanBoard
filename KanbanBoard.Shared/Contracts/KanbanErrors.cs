namespace KanbanBoard.Shared.Contracts;

/// <summary>
/// Stable machine-readable codes for refused calls. The API returns them as <see cref="KanbanErrorDto"/>
/// and the MCP server returns the same shape to tool callers. Codes are a contract: add new ones, never rename.
/// </summary>
public static class KanbanErrorCodes
{
    /// <summary>The caller's token or identity is not allowed to make this call.</summary>
    public const string ScopeDenied = "scope_denied";

    /// <summary>The addressed entity does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>The work item has a live claim held by another claimant or marker.</summary>
    public const string AlreadyClaimed = "already_claimed";

    /// <summary>The entity is no longer in the state the call depends on; nothing was changed.</summary>
    public const string Conflict = "conflict";

    /// <summary>An argument is missing or out of range.</summary>
    public const string InvalidArgument = "invalid_argument";
}

public sealed record KanbanErrorDto(string Code, string Message);

public static class WorkItemClaimLimits
{
    public const int MaxMarkerLength = 200;
    public const int MaxClaimantNameLength = 320;
    public const int MinTtlSeconds = 1;
    public const int MaxTtlSeconds = 86_400;
}
