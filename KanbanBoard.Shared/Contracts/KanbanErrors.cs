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

/// <summary>The kinds of entry in the change feed. Kinds are a contract: add new ones, never rename.</summary>
public static class WorkItemChangeKinds
{
    public const string ItemCreated = "item_created";
    /// <summary>A field update, and an item taken out of its epic because the epic was archived or deleted.</summary>
    public const string ItemUpdated = "item_updated";
    public const string ItemMoved = "item_moved";
    public const string ItemDeleted = "item_deleted";
    public const string Claimed = "claimed";
    public const string ClaimRenewed = "claim_renewed";
    public const string ClaimReleased = "claim_released";
    public const string CommentAdded = "comment_added";
    public const string LinkAdded = "link_added";
    public const string LinkRemoved = "link_removed";
    public const string DependencyAdded = "dependency_added";
    public const string DependencyRemoved = "dependency_removed";

    public static readonly IReadOnlyList<string> All =
    [
        ItemCreated, ItemUpdated, ItemMoved, ItemDeleted, Claimed, ClaimRenewed, ClaimReleased,
        CommentAdded, LinkAdded, LinkRemoved, DependencyAdded, DependencyRemoved
    ];
}

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

public static class WorkItemLimits
{
    public const int MaxTitleLength = 240;

    /// <summary>The longest a work item's labels may be together, joined with commas.</summary>
    public const int MaxLabelsLength = 4000;
}

public static class WorkItemCommentLimits
{
    /// <summary>As long as a claimant name: a token's comment carries the same name as its claims.</summary>
    public const int MaxAuthorLength = WorkItemClaimLimits.MaxClaimantNameLength;
    public const int MaxIdempotencyKeyLength = IdempotencyKeyLimits.MaxLength;
}

/// <summary>The idempotency key of every create that takes one.</summary>
public static class IdempotencyKeyLimits
{
    public const int MaxLength = 128;
}

public static class ProjectLimits
{
    public const int MaxNameLength = 160;
    public const int MaxKeyLength = 24;
    public const int MaxRepositoryUrlLength = WorkItemLinkLimits.MaxUrlLength;
    public const int MaxDefaultBranchLength = 255;
}

public static class WorkItemClaimLimits
{
    public const int MaxMarkerLength = 200;
    public const int MaxClaimantNameLength = 320;
    public const int MinTtlSeconds = 1;
    public const int MaxTtlSeconds = 86_400;
}
