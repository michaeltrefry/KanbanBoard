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

    /// <summary>The dependency would make a work item block itself, directly or through other items; nothing was changed.</summary>
    public const string Cycle = "cycle";

    /// <summary>The server could not take the call in time because of other callers; nothing was changed and the call can be retried.</summary>
    public const string Busy = "busy";
}

public sealed record KanbanErrorDto(string Code, string Message);

public static class WorkItemLinkKinds
{
    public const string PullRequest = "pr";
    public const string Branch = "branch";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [PullRequest, Branch, Other];
}

public static class WorkItemLinkLimits
{
    public const int MaxKindLength = 16;
    public const int MaxUrlLength = 2048;

    /// <summary>
    /// The longest percent-encoded form (<see cref="Uri.EscapeDataString(string)"/>) a link URL may have. Find and
    /// remove carry the URL in the query string, and the request line of the API is limited to 8 KB. Three times
    /// <see cref="MaxUrlLength"/> lets every ASCII URL of the allowed length through.
    /// </summary>
    public const int MaxEscapedUrlLength = 3 * MaxUrlLength;

    public static readonly string LookupUrlTooLongMessage =
        $"Argument 'url' must be at most {MaxEscapedUrlLength} characters when percent-encoded.";

    /// <summary>Whether <paramref name="url"/> is short enough, percent-encoded, to be looked up.</summary>
    public static bool FitsLookup(string url) =>
        url.Length <= MaxEscapedUrlLength && Uri.EscapeDataString(url).Length <= MaxEscapedUrlLength;
}

public static class WorkItemClaimLimits
{
    public const int MaxMarkerLength = 200;
    public const int MaxClaimantNameLength = 320;
    public const int MinTtlSeconds = 1;
    public const int MaxTtlSeconds = 86_400;
}
