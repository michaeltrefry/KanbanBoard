using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Mcp.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace KanbanBoard.Mcp.Tools;

[McpServerToolType]
public sealed class KanbanTools(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    IOptions<McpAuthenticationOptions> authenticationOptions)
{
    public const string MissingTokenErrorMessage =
        "This request has no authenticated personal access token, so write tools are unavailable.";

    public const string ReadOnlyScopeErrorMessage =
        "This personal access token has the 'read' scope and cannot create, update, move, claim, comment on, or delete data. " +
        "Create a token with the 'full' scope in Kanban settings to use write tools.";

    public sealed record DeleteResultDto(Guid Id, string EntityType, bool Deleted);

    private const string RepositoryUrlDescription =
        "Repository the project's work is done in: an http or https URL without user name or password (https://github.com/owner/repo.git), " +
        "or an SSH URL without password (ssh://git@github.com/owner/repo.git or git@github.com:owner/repo.git). At most 2048 characters, stored as given.";

    private const string DefaultBranchDescription =
        "Branch of the repository that work starts from, such as main: a branch name git accepts, at most 255 characters";

    private const string UnblockedOnlyDescription =
        "When true, return only items that are not waiting on a blocker: every item they are blocked by is Done (items with no blockers included)";

    private const string LabelListDescription =
        "Labels as a list, in place of labels (passing both is refused with code invalid_argument). Each label is a non-empty string without a comma; " +
        "all labels together, joined with commas, are at most 4000 characters. Duplicates, ignoring case, are dropped.";

    private const string ExpectedVersionDescription =
        "The version field of the item as it was last read. When passed, the call is refused with code conflict, changing nothing, unless the item still has that version.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool, Description("List all projects in the kanban board. Each project carries repository_url and default_branch, the repository and branch its work is done in; both are null when not set.")]
    public async Task<IReadOnlyList<ProjectSummaryDto>> ListProjects(
        [Description("Whether to include archived projects")] bool includeArchived = false)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        return await client.GetFromJsonAsync<IReadOnlyList<ProjectSummaryDto>>($"/api/projects?includeArchived={includeArchived}", JsonOptions)
            ?? [];
    }

    [McpServerTool, Description("Create a new project, optionally with the repository and branch its work is done in. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key, nothing is created and the project it created is returned as it is now, whatever the other arguments. Returns the project with created (false for a repeat). A key another project has is refused with code conflict, a malformed argument with invalid_argument, and a keyed create that waited 10 seconds for other keyed creates with busy; retry.")]
    public async Task<ProjectSummaryDto?> CreateProject(
        [Description("Project name, at most 160 characters")] string name,
        [Description("Short key, at most 24 characters, stored in upper case. No two projects share a key.")] string key,
        [Description("Optional description")] string? description = null,
        [Description(RepositoryUrlDescription)] string? repository_url = null,
        [Description(DefaultBranchDescription)] string? default_branch = null,
        [Description("Caller-chosen key for this project, such as a plan id: 1 to 128 printable ASCII characters without spaces, compared exactly. It belongs to the calling identity.")] string? idempotency_key = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, key, description, false, repository_url, default_branch, idempotency_key));
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<ProjectSummaryDto>(JsonOptions);
    }

    [McpServerTool, Description("Change a project. Only the arguments that are passed are changed: an argument that is left out, or passed as null, keeps its value. To empty an optional field, name it in clear (description, repository_url, default_branch); an empty string is refused with code invalid_argument, as is a field that is both passed and named in clear. Renames with name, archives with is_archived true and restores with is_archived false; an archived project is left out of list_projects unless includeArchived is true. Returns the project. An unknown id is refused with code not_found, a key another project has with conflict.")]
    public async Task<ProjectSummaryDto> UpdateProject(
        [Description("Project id")] Guid id,
        [Description("New name, at most 160 characters. Several projects may share a name.")] string? name = null,
        [Description("New key, at most 24 characters, stored in upper case. No two projects share a key.")] string? key = null,
        [Description("New description")] string? description = null,
        [Description(RepositoryUrlDescription)] string? repository_url = null,
        [Description(DefaultBranchDescription)] string? default_branch = null,
        [Description("true archives the project, false restores it")] bool? is_archived = null,
        [Description("Names of the optional fields to empty: any of description, repository_url, default_branch")] string[]? clear = null)
    {
        EnsureWriteScope();
        var body = PatchBody(
            clear,
            ("name", "name", name, false, false),
            ("key", "key", key, false, false),
            ("description", "description", description, true, false),
            ("repository_url", "repositoryUrl", repository_url, true, false),
            ("default_branch", "defaultBranch", default_branch, true, false));
        if (is_archived is not null)
        {
            body["isArchived"] = is_archived.Value;
        }

        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.PatchAsync($"/api/projects/{id}", JsonContent.Create(body));
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<ProjectSummaryDto>(JsonOptions)
            ?? throw new InvalidOperationException($"The Kanban API returned no project for {id}.");
    }

    [McpServerTool, Description("Fetch a project board, optionally filtered to a single status. Closed items stay persisted but are hidden unless status=Closed is requested.")]
    public async Task<ProjectBoardDto?> GetBoard(
        [Description("Project id")] Guid projectId,
        [Description("Optional status filter")] WorkItemStatus? status = null,
        [Description("Whether to include items that belong to archived epics")] bool includeArchivedEpics = false)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        var path = $"/api/projects/{projectId}";
        var parameters = new List<string>();
        if (status is not null)
        {
            parameters.Add($"status={status}");
        }

        if (includeArchivedEpics)
        {
            parameters.Add("includeArchivedEpics=true");
        }

        if (parameters.Count > 0)
        {
            path = $"{path}?{string.Join('&', parameters)}";
        }

        return await client.GetFromJsonAsync<ProjectBoardDto>(path, JsonOptions);
    }

    [McpServerTool, Description("List epics that belong to a project.")]
    public async Task<IReadOnlyList<EpicDto>> ListEpics(
        [Description("Project id")] Guid projectId,
        [Description("Whether to include archived epics")] bool includeArchived = false)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        return await client.GetFromJsonAsync<IReadOnlyList<EpicDto>>($"/api/projects/{projectId}/epics?includeArchived={includeArchived}", JsonOptions) ?? [];
    }

    [McpServerTool, Description("Fetch a single epic.")]
    public async Task<EpicDto?> GetEpic(
        [Description("Epic id")] Guid epicId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        return await client.GetFromJsonAsync<EpicDto>($"/api/epics/{epicId}", JsonOptions);
    }

    [McpServerTool, Description("Create an epic in a project.")]
    public async Task<EpicDto?> CreateEpic(
        [Description("Project id")] Guid projectId,
        [Description("Epic name")] string name,
        [Description("Optional description")] string? description = null,
        [Description("Whether the epic starts archived")] bool isArchived = false)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/epics", new CreateEpicRequest(name, description, isArchived));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EpicDto>(JsonOptions);
    }

    [McpServerTool, Description("Update an epic.")]
    public async Task<EpicDto?> UpdateEpic(
        [Description("Epic id")] Guid epicId,
        [Description("Epic name")] string name,
        [Description("Optional description")] string? description = null,
        [Description("Whether the epic is archived")] bool isArchived = false)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PutAsJsonAsync($"/api/epics/{epicId}", new UpdateEpicRequest(name, description, isArchived));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EpicDto>(JsonOptions);
    }

    [McpServerTool, Description("Delete a project.")]
    public async Task<DeleteResultDto> DeleteProject(
        [Description("Project id")] Guid projectId)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.DeleteAsync($"/api/projects/{projectId}");
        response.EnsureSuccessStatusCode();
        return new DeleteResultDto(projectId, "project", true);
    }

    [McpServerTool, Description("Delete an epic.")]
    public async Task<DeleteResultDto> DeleteEpic(
        [Description("Epic id")] Guid epicId)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.DeleteAsync($"/api/epics/{epicId}");
        response.EnsureSuccessStatusCode();
        return new DeleteResultDto(epicId, "epic", true);
    }

    [McpServerTool, Description("List documents that belong to an epic.")]
    public async Task<IReadOnlyList<EpicDocumentDto>> ListEpicDocuments(
        [Description("Epic id")] Guid epicId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        return await client.GetFromJsonAsync<IReadOnlyList<EpicDocumentDto>>($"/api/epics/{epicId}/documents", JsonOptions) ?? [];
    }

    [McpServerTool, Description("Fetch a single epic document.")]
    public async Task<EpicDocumentDto?> GetEpicDocument(
        [Description("Epic document id")] Guid documentId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        return await client.GetFromJsonAsync<EpicDocumentDto>($"/api/epic-documents/{documentId}", JsonOptions);
    }

    [McpServerTool, Description("Create a document for an epic.")]
    public async Task<EpicDocumentDto?> CreateEpicDocument(
        [Description("Epic id")] Guid epicId,
        [Description("Document title")] string title,
        [Description("Document body")] string body)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync($"/api/epics/{epicId}/documents", new CreateEpicDocumentRequest(title, body));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EpicDocumentDto>(JsonOptions);
    }

    [McpServerTool, Description("Update an epic document.")]
    public async Task<EpicDocumentDto?> UpdateEpicDocument(
        [Description("Epic document id")] Guid documentId,
        [Description("Document title")] string title,
        [Description("Document body")] string body)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PutAsJsonAsync($"/api/epic-documents/{documentId}", new UpdateEpicDocumentRequest(title, body));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EpicDocumentDto>(JsonOptions);
    }

    [McpServerTool, Description("List work items, optionally filtered by project, epic, type, status, label, claim state and time of last change. Closed items stay persisted but are hidden unless status=Closed is requested. Each item lists its blockers and dependents with their current status, and its external links, and carries estimate (null when not set), label_list (its labels as a list) and version (see update_work_item). Without limit and cursor the result is an array of every matching item, in board order. With limit or cursor it is one page, oldest item first: an object {\"items\": [...], \"next_cursor\": \"...\"}, where next_cursor is left out on the last page. A malformed argument is refused with code invalid_argument.")]
    public async Task<object> ListWorkItems(
        [Description("Optional project id")] Guid? projectId = null,
        [Description("Optional epic id")] Guid? epicId = null,
        [Description("Optional work item type: Story, Issue, or Task")] WorkItemType? type = null,
        [Description("Optional status filter")] WorkItemStatus? status = null,
        [Description("Whether to include items that belong to archived epics")] bool includeArchivedEpics = false,
        [Description(UnblockedOnlyDescription)] bool unblocked_only = false,
        [Description("Return only items that carry this label. The whole label must match, ignoring case: 'api' does not match an item labelled 'rapid'.")] string? label = null,
        [Description("ISO 8601 timestamp, for example 2026-10-12T12:00:00Z. Return only items whose updatedAtUtc is at or after it. updatedAtUtc changes when the item's fields change, when it is moved, when a comment is added to it, or when a link is added to or removed from it; it does not change when the item is claimed, renewed or released, when a claim expires, or when a dependency or another item changes.")] string? updated_since = null,
        [Description("Claim state at the time of the call: 'unclaimed' (no live claim, including a claim that has expired), 'claimed' (a live claim), or 'expired' (a claim that expired and was not released or replaced)")] string? claim_state = null,
        [Description("Page size, 1 to 200. Passing limit or cursor makes the result a page object instead of an array.")] int? limit = null,
        [Description("next_cursor of the previous page, passed back unchanged together with the same filters. Each matching item is returned exactly once across the pages, also when items are moved or edited in between. Page size is 50 when limit is not passed.")] string? cursor = null,
        [Description("When false, every item is returned with an empty comments list and no comment is read")] bool include_comments = true)
    {
        var added = new List<string>();
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                added.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        Add("label", label);
        Add("updatedSince", updated_since);
        Add("claimState", claim_state);
        Add("limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("cursor", cursor);
        Add("includeComments", include_comments ? null : "false");

        if (limit is null && cursor is null)
        {
            return await ListWorkItemsAsync<IReadOnlyList<WorkItemDto>>(projectId, epicId, type, status, includeArchivedEpics, unblocked_only, added) ?? [];
        }

        return await ListWorkItemsAsync<WorkItemPageDto>(projectId, epicId, type, status, includeArchivedEpics, unblocked_only, added)
            ?? new WorkItemPageDto([], null);
    }

    private async Task<TResult?> ListWorkItemsAsync<TResult>(
        Guid? projectId = null,
        Guid? epicId = null,
        WorkItemType? type = null,
        WorkItemStatus? status = null,
        bool includeArchivedEpics = false,
        bool unblockedOnly = false,
        IEnumerable<string>? added = null)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        var parameters = new List<string>();

        if (projectId is not null)
        {
            parameters.Add($"projectId={projectId}");
        }

        if (epicId is not null)
        {
            parameters.Add($"epicId={epicId}");
        }

        if (type is not null)
        {
            parameters.Add($"type={type}");
        }

        if (status is not null)
        {
            parameters.Add($"status={status}");
        }

        if (includeArchivedEpics)
        {
            parameters.Add("includeArchivedEpics=true");
        }

        if (unblockedOnly)
        {
            parameters.Add("unblockedOnly=true");
        }

        // The arguments this tool gained after its first release, after the original ones.
        parameters.AddRange(added ?? []);

        var path = parameters.Count == 0
            ? "/api/items"
            : $"/api/items?{string.Join('&', parameters)}";

        using var response = await client.GetAsync(path);
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<TResult>(JsonOptions);
    }

    [McpServerTool, Description("Fetch a single work item by id, including its description, status, comments, its blockers and dependents with their current status, and its external links. estimate is null when not set; label_list is the labels as a list (labels is the same labels as one comma-separated string); version counts the updates and moves of the item, and can be passed to update_work_item and move_work_item as expected_version.")]
    public async Task<WorkItemDto> GetWorkItem(
        [Description("Work item id")] Guid itemId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.GetAsync($"/api/items/{itemId}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw KanbanToolErrors.NotFound($"Work item {itemId} was not found.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions)
            ?? throw KanbanToolErrors.NotFound($"Work item {itemId} was not found.");
    }

    [McpServerTool, Description("Claim a work item for the calling token's identity until ttl_seconds from now. Succeeds when the item is unclaimed, its claim has expired, or the caller already holds it under the same marker (which resets the expiry). Refused with code already_claimed when another identity or another marker holds a live claim.")]
    public Task<WorkItemDto> ClaimWorkItem(
        [Description("Work item id")] Guid id,
        [Description("Caller-chosen id for this claim, such as a run id (max 200 characters, compared exactly). Renew and release must pass the same marker.")] string marker,
        [Description("Seconds until the claim lapses unless renewed (1 to 86400)")] int ttl_seconds) =>
        SendWorkItemWriteAsync(client => client.PostAsJsonAsync($"/api/items/{id}/claim", new ClaimWorkItemRequest(marker, ttl_seconds)));

    [McpServerTool, Description("Extend a live claim the caller holds under this marker to ttl_seconds from now. Refused with code conflict when the claim has lapsed or was released, and already_claimed when someone else now holds the item.")]
    public Task<WorkItemDto> RenewWorkItemClaim(
        [Description("Work item id")] Guid id,
        [Description("Marker the claim was made with")] string marker,
        [Description("Seconds until the claim lapses unless renewed again (1 to 86400)")] int ttl_seconds) =>
        SendWorkItemWriteAsync(client => client.PostAsJsonAsync($"/api/items/{id}/claim/renew", new ClaimWorkItemRequest(marker, ttl_seconds)));

    [McpServerTool, Description("Release the caller's claim on a work item so others can claim it. Refused with code conflict when the caller holds no claim under this marker, and already_claimed when someone else holds the item.")]
    public Task<WorkItemDto> ReleaseWorkItemClaim(
        [Description("Work item id")] Guid id,
        [Description("Marker the claim was made with")] string marker) =>
        SendWorkItemWriteAsync(client => client.PostAsJsonAsync($"/api/items/{id}/claim/release", new ReleaseWorkItemClaimRequest(marker)));

    [McpServerTool, Description("Record that work item id is blocked by work item blocked_by_id. Adding a dependency that already exists changes nothing and succeeds. Refused with code cycle, changing nothing, when it would make an item block itself directly or through other items. Refused with code busy, changing nothing, when other dependency changes kept it waiting 10 seconds; retry. Returns work item id with its blockers and dependents.")]
    public Task<WorkItemDto> AddWorkItemDependency(
        [Description("Id of the work item that is blocked")] Guid id,
        [Description("Id of the work item that blocks it")] Guid blocked_by_id) =>
        SendWorkItemWriteAsync(client => client.PostAsJsonAsync($"/api/items/{id}/dependencies", new AddWorkItemDependencyRequest(blocked_by_id)));

    [McpServerTool, Description("Remove the dependency that makes work item id blocked by work item blocked_by_id. Removing a dependency that does not exist changes nothing and succeeds. Returns work item id with its blockers and dependents.")]
    public Task<WorkItemDto> RemoveWorkItemDependency(
        [Description("Id of the work item that is blocked")] Guid id,
        [Description("Id of the work item that blocks it")] Guid blocked_by_id) =>
        SendWorkItemWriteAsync(client => client.DeleteAsync($"/api/items/{id}/dependencies/{blocked_by_id}"));

    [McpServerTool, Description("Attach an external URL, such as a pull request or a branch, to work item id. kind is pr, branch or other; any other kind is refused with code invalid_argument, as is a url that is not an absolute http or https URL of at most 2048 characters, that holds whitespace, a control character or credentials (user:password@), or whose percent-encoded form is longer than 6144 characters (every ASCII URL of 2048 characters fits; a non-ASCII character counts for 6 to 9). Adding a kind and url the item already carries changes nothing and succeeds. The same url may be attached to several items. Returns work item id; its links field lists each link as {id, kind, url, created_at_utc}.")]
    public Task<WorkItemDto> AddWorkItemLink(
        [Description("Work item id")] Guid id,
        [Description("Kind of link: pr, branch or other")] string kind,
        [Description("Absolute http or https URL, at most 2048 characters, without whitespace or credentials. URLs are compared exactly, character for character.")] string url) =>
        SendWorkItemWriteAsync(client => client.PostAsJsonAsync($"/api/items/{id}/links", new AddWorkItemLinkRequest(kind, url)));

    [McpServerTool, Description("Remove one link from work item id. Address the link either by link_id (the id of an entry of the item's links field) or by kind and url together; any other combination is refused with code invalid_argument, as is a url whose percent-encoded form is longer than 6144 characters. Removing a link the item does not carry changes nothing and succeeds. Returns work item id with its remaining links.")]
    public Task<WorkItemDto> RemoveWorkItemLink(
        [Description("Work item id")] Guid id,
        [Description("Id of the link to remove. Leave out when passing kind and url.")] Guid? link_id = null,
        [Description("Kind of the link to remove: pr, branch or other. Pass together with url.")] string? kind = null,
        [Description("URL of the link to remove, exactly as it was added. Pass together with kind.")] string? url = null)
    {
        // The API drops surrounding whitespace as well; dropping it here keeps it out of the length that is checked.
        url = url?.Trim();
        var parameters = new List<string>();
        if (link_id is not null)
        {
            parameters.Add($"linkId={link_id}");
        }

        if (kind is not null)
        {
            parameters.Add($"kind={Uri.EscapeDataString(kind)}");
        }

        if (url is not null)
        {
            parameters.Add($"url={Uri.EscapeDataString(url)}");
        }

        // The scope check comes first; a URL too long for the request line never reaches the API.
        return SendWorkItemWriteAsync(client => url is null || WorkItemLinkLimits.FitsLookup(url)
            ? client.DeleteAsync($"/api/items/{id}/links?{string.Join('&', parameters)}")
            : throw KanbanToolErrors.InvalidArgument(WorkItemLinkLimits.LookupUrlTooLongMessage));
    }

    [McpServerTool, Description("Find the work items that carry an external link with exactly this URL, under any kind and in any status, project or epic, oldest item first. Returns full work items, and an empty array when no item carries the URL. An empty url, or one whose percent-encoded form is longer than 6144 characters, is refused with code invalid_argument.")]
    public async Task<IReadOnlyList<WorkItemDto>> FindWorkItemsByLink(
        [Description("The URL, exactly as it was added: URLs are compared character for character")] string url)
    {
        // A URL too long for the request line never reaches the API. The API drops surrounding whitespace as well.
        url = url.Trim();
        if (!WorkItemLinkLimits.FitsLookup(url))
        {
            throw KanbanToolErrors.InvalidArgument(WorkItemLinkLimits.LookupUrlTooLongMessage);
        }

        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.GetAsync($"/api/items/by-link?url={Uri.EscapeDataString(url)}");
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<WorkItemDto>>(JsonOptions) ?? [];
    }

    [McpServerTool, Description("Search work items whose title or description contains every word in the query (case-insensitive). Returns ids, project ids, titles, types, statuses, estimates (null when not set) and versions only (no bodies), including closed items, capped at a page size. Use GetWorkItem for full details.")]
    public async Task<IReadOnlyList<WorkItemSearchResultDto>> SearchWorkItems(
        [Description("Words to match in work item titles and descriptions")] string query,
        [Description("Optional project id to limit the search to")] Guid? projectId = null,
        [Description("Optional maximum number of results (default 25, max 100)")] int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ModelContextProtocol.McpException("Search query is required.");
        }

        var client = httpClientFactory.CreateClient("kanban-api");
        var parameters = new List<string> { $"query={Uri.EscapeDataString(query)}" };
        if (projectId is not null)
        {
            parameters.Add($"projectId={projectId}");
        }

        if (limit is not null)
        {
            parameters.Add($"limit={limit}");
        }

        return await client.GetFromJsonAsync<IReadOnlyList<WorkItemSearchResultDto>>(
            $"/api/items/search?{string.Join('&', parameters)}",
            JsonOptions) ?? [];
    }

    [McpServerTool, Description("List work items that belong to a specific epic backlog.")]
    public async Task<IReadOnlyList<WorkItemDto>> ListEpicWorkItems(
        [Description("Epic id")] Guid epicId,
        [Description("Optional work item type: Story, Issue, or Task")] WorkItemType? type = null,
        [Description("Optional status filter")] WorkItemStatus? status = null,
        [Description("Whether to include items when the epic is archived")] bool includeArchivedEpics = false,
        [Description(UnblockedOnlyDescription)] bool unblocked_only = false) =>
        await ListWorkItemsAsync<IReadOnlyList<WorkItemDto>>(epicId: epicId, type: type, status: status, includeArchivedEpics: includeArchivedEpics, unblockedOnly: unblocked_only) ?? [];

    [McpServerTool, Description("Fetch issue work items, optionally filtered by project and status.")]
    public async Task<IReadOnlyList<WorkItemDto>> GetIssues(
        [Description("Optional project id")] Guid? projectId = null,
        [Description("Optional status filter")] WorkItemStatus? status = null,
        [Description(UnblockedOnlyDescription)] bool unblocked_only = false)
    {
        return await ListWorkItemsAsync<IReadOnlyList<WorkItemDto>>(projectId: projectId, type: WorkItemType.Issue, status: status, unblockedOnly: unblocked_only) ?? [];
    }

    [McpServerTool, Description("Add a comment to any work item. The author of a comment added with a personal access token is the token's identity: the comment carries that identity's name as author and its id as author_id, and the author argument is ignored. Without a token, author is required. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key on this item, nothing is added, the item's updatedAtUtc does not change, and the earlier comment is reported whatever its body. Returns the work item with all its comments, plus comment_id (the id of the comment that was added, or of the earlier one) and comment_created (false for a repeat). An unknown itemId is refused with code not_found, a malformed argument with invalid_argument.")]
    public async Task<WorkItemDto?> AddWorkItemComment(
        [Description("Work item id")] Guid itemId,
        [Description("Comment text")] string body,
        [Description("Comment author, used only when the call carries no personal access token. With a token the author is the token's identity and this is ignored.")] string? author = null,
        [Description("Caller-chosen key for this comment, such as a run id plus a step name: 1 to 128 printable ASCII characters without spaces, compared exactly. It belongs to the calling identity and this work item.")] string? idempotency_key = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.PostAsJsonAsync($"/api/items/{itemId}/comments", new CreateWorkItemCommentRequest(author, body, idempotency_key));
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Add a comment to an issue. Same as add_work_item_comment without an idempotency key: with a personal access token the author is the token's identity and the author argument is ignored.")]
    public Task<WorkItemDto?> AddIssueComment(
        [Description("Issue work item id")] Guid itemId,
        [Description("Comment author, used only when the call carries no personal access token")] string author,
        [Description("Comment text")] string body) =>
        AddWorkItemComment(itemId, body, author);

    [McpServerTool, Description("List the comments of work item id, oldest first, one page at a time: an object {\"items\": [...], \"next_cursor\": \"...\"}, where next_cursor is left out on the last page. Each comment is {id, workItemId, author, author_id, body, createdAtUtc}: author_id is the id of the identity whose token wrote the comment and is left out for a comment written without a token; author is that identity's name when the comment was written, or the name the writer gave. An unknown id is refused with code not_found, a limit outside 1 to 200 or a malformed cursor with invalid_argument.")]
    public async Task<WorkItemCommentPageDto> ListWorkItemComments(
        [Description("Work item id")] Guid id,
        [Description("Page size, 1 to 200. 50 when not passed.")] int? limit = null,
        [Description("next_cursor of the previous page, passed back unchanged. Each comment is returned exactly once across the pages, also when comments are added in between: a new comment always comes after every earlier one.")] string? cursor = null)
    {
        var parameters = new List<string>();
        if (limit is not null)
        {
            parameters.Add($"limit={limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (cursor is not null)
        {
            parameters.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.GetAsync($"/api/items/{id}/comments{(parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters))}");
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemCommentPageDto>(JsonOptions)
            ?? new WorkItemCommentPageDto([], null);
    }

    [McpServerTool, Description("Read the change feed: every change to a work item, oldest first, as an object {\"items\": [...], \"next_cursor\": \"...\"}. next_cursor is always set, also when items is empty: pass it as cursor to the next call to get the entries written since, each exactly once and in the order they were written. Called without cursor it returns no entries and the cursor of the newest entry, which is where to start following from; from_start true starts at the oldest entry instead. Reading again from the same cursor returns the same entries; fewer entries than limit means the end of the feed was reached. Each entry is {sequence, item_id, project_id, kind, actor, changes, created_at_utc}. kind is one of item_created, item_updated, item_moved, item_deleted, claimed, claim_renewed, claim_released, comment_added, link_added, link_removed, dependency_added, dependency_removed. actor is {id, name} of the identity whose token or browser session made the change, and is left out when the write carried neither. changes lists {field, old, new} for each field the write changed: the item's own fields (title, description, type, status, priority, order, estimate, labels, epic_id, version), the stored claim (claimant_id, claimant, claim_marker, claim_expires_at_utc), comment ({id, author, body}), link ({id, kind, url}), or blocked_by (the id of the blocking item; the entry is on the blocked item). Entries stay after their item is deleted. A write that was refused or changed nothing has no entry, and neither has a change to a project or an epic itself. A malformed cursor, a cursor of another tool, cursor together with from_start, or a limit outside 1 to 200 is refused with code invalid_argument.")]
    public async Task<WorkItemChangePageDto> ListChanges(
        [Description("next_cursor of an earlier call, passed back unchanged")] string? cursor = null,
        [Description("Only entries of work items of this project. The cursor is a position in the whole feed, so one cursor can be used with any project_id or with none.")] Guid? project_id = null,
        [Description("Most entries to return, 1 to 200. 50 when not passed.")] int? limit = null,
        [Description("true reads from the oldest entry of the feed. Cannot be combined with cursor.")] bool from_start = false)
    {
        var parameters = new List<string>();
        if (cursor is not null)
        {
            parameters.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        if (project_id is not null)
        {
            parameters.Add($"projectId={project_id}");
        }

        if (limit is not null)
        {
            parameters.Add($"limit={limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (from_start)
        {
            parameters.Add("fromStart=true");
        }

        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.GetAsync($"/api/items/changes{(parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters))}");
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemChangePageDto>(JsonOptions)
            ?? throw new InvalidOperationException("The Kanban API returned no page of the change feed.");
    }

    [McpServerTool, Description("Create a story, issue, or task in a project backlog. Pass epicId to attach it to a specific epic backlog.")]
    public async Task<WorkItemDto?> CreateWorkItem(
        [Description("Project id")] Guid projectId,
        [Description("Title")] string title,
        [Description("Type: Story, Issue, or Task")] WorkItemType type,
        [Description("Status")] WorkItemStatus status = WorkItemStatus.Backlog,
        [Description("Priority")] WorkItemPriority priority = WorkItemPriority.Medium,
        [Description("Optional epic id")] Guid? epicId = null,
        [Description("Optional description")] string? description = null,
        [Description("Optional estimate")] int? estimate = null,
        [Description("Optional comma-separated labels")] string? labels = null,
        [Description(LabelListDescription)] string[]? label_list = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.PostAsJsonAsync("/api/items", new CreateWorkItemRequest(
            projectId,
            epicId,
            title,
            description,
            type,
            status,
            priority,
            estimate,
            labels,
            label_list));

        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Create a story, issue, or task directly in an epic backlog.")]
    public async Task<WorkItemDto?> CreateEpicWorkItem(
        [Description("Epic id")] Guid epicId,
        [Description("Title")] string title,
        [Description("Type: Story, Issue, or Task")] WorkItemType type = WorkItemType.Story,
        [Description("Status")] WorkItemStatus status = WorkItemStatus.Backlog,
        [Description("Priority")] WorkItemPriority priority = WorkItemPriority.Medium,
        [Description("Optional description")] string? description = null,
        [Description("Optional estimate")] int? estimate = null,
        [Description("Optional comma-separated labels")] string? labels = null,
        [Description(LabelListDescription)] string[]? label_list = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var epic = await GetRequiredEpicAsync(client, epicId);

        using var response = await client.PostAsJsonAsync("/api/items", new CreateWorkItemRequest(
            epic.ProjectId,
            epicId,
            title,
            description,
            type,
            status,
            priority,
            estimate,
            labels,
            label_list));

        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Move a work item to a column and a position in it; the items it displaces are renumbered and are otherwise unchanged. Pass expected_version to refuse the move with code conflict, changing nothing, when the item was updated or moved after it was read. Returns the work item with its new version. An unknown itemId is refused with code not_found.")]
    public async Task<WorkItemDto?> MoveWorkItem(
        [Description("Work item id")] Guid itemId,
        [Description("Target status")] WorkItemStatus status,
        [Description("Zero-based order in the target column")] int order = 0,
        [Description(ExpectedVersionDescription)] long? expected_version = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.PostAsJsonAsync($"/api/items/{itemId}/move", new MoveWorkItemRequest(status, order, expected_version));
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Change a work item. Only the arguments that are passed are changed: an argument that is left out, or passed as null, keeps its value, so update_work_item(itemId, status) changes the status and nothing else. To empty an optional field, name it in clear (description, estimate, labels, epicId); an empty string or an empty label_list is refused with code invalid_argument, as are a field that is both passed and named in clear and a call that passes no field to change. One exception, kept for callers from before clear existed: description passed as an empty or whitespace-only string leaves the item with an empty description, and labels passed as a string with no label in it (\"\", \" \", \" , \") leaves it without labels. A new status puts the item at the end of that column. Pass expected_version to refuse the update with code conflict, changing nothing, when the item was updated or moved after it was read. Returns the work item with its new version: version goes up by one with every update and every move of the item, and not when it is claimed, commented on, linked, given a dependency, or when another item is reordered around it. An unknown itemId is refused with code not_found.")]
    public async Task<WorkItemDto?> UpdateWorkItem(
        [Description("Work item id")] Guid itemId,
        [Description("New title, at most 240 characters")] string? title = null,
        [Description("New type: Story, Issue, or Task")] WorkItemType? type = null,
        [Description("New status")] WorkItemStatus? status = null,
        [Description("New priority")] WorkItemPriority? priority = null,
        [Description("Id of the epic to put the item in: an active epic of the item's project")] Guid? epicId = null,
        [Description("New description")] string? description = null,
        [Description("New estimate")] int? estimate = null,
        [Description("New labels as one comma-separated string. They replace the labels the item has.")] string? labels = null,
        [Description("New " + LabelListDescription + " They replace the labels the item has.")] string[]? label_list = null,
        [Description(ExpectedVersionDescription)] long? expected_version = null,
        [Description("Names of the optional fields to empty: any of description, estimate, labels, epicId")] string[]? clear = null)
    {
        EnsureWriteScope();
        JsonNode? newLabels = labels;
        if (label_list is not null)
        {
            if (labels is not null)
            {
                throw KanbanToolErrors.InvalidArgument("Arguments 'labels' and 'label_list' are both passed. Do one of the two.");
            }

            if (label_list.Length == 0)
            {
                throw KanbanToolErrors.InvalidArgument("Argument 'label_list' must not be empty. To empty the field, name labels in 'clear'.");
            }

            if (label_list.Any(label => string.IsNullOrWhiteSpace(label) || label.Contains(',')))
            {
                throw KanbanToolErrors.InvalidArgument("Every label of 'label_list' must be a non-empty string without a comma.");
            }

            newLabels = new JsonArray(label_list.Select(label => (JsonNode?)label).ToArray());
        }

        var body = PatchBody(
            clear,
            ("title", "title", title, false, false),
            ("type", "type", type?.ToString(), false, false),
            ("status", "status", status?.ToString(), false, false),
            ("priority", "priority", priority?.ToString(), false, false),
            ("epicId", "epicId", epicId?.ToString(), true, false),
            // description and labels are the two strings this tool took before clear existed, when an empty one
            // emptied the field. That still works: the API stores an empty description and no labels.
            ("description", "description", description, true, true),
            ("estimate", "estimate", estimate, true, false),
            ("labels", "labels", newLabels, true, true));
        if (body.Count == 0)
        {
            // An update of nothing would still advance the version, and make another caller's guarded write fail.
            throw KanbanToolErrors.InvalidArgument("Nothing to change: pass at least one field to change, or name one in 'clear'.");
        }

        if (expected_version is not null)
        {
            body["expectedVersion"] = expected_version.Value;
        }

        // The item is addressed by its id: no board is read to find it.
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.PatchAsync($"/api/items/{itemId}", JsonContent.Create(body));
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Delete a work item.")]
    public async Task<DeleteResultDto> DeleteWorkItem(
        [Description("Work item id")] Guid itemId)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.DeleteAsync($"/api/items/{itemId}");
        response.EnsureSuccessStatusCode();
        return new DeleteResultDto(itemId, "work_item", true);
    }

    // The body of a partial update, and the one convention for emptying an optional field over MCP: an argument
    // that is left out (or null: MCP cannot tell the two apart) is not sent and keeps its value, an argument with a
    // value is sent, and a field named in clear is sent as null. Each field is (argument name, API name, value as
    // JSON, whether clear may name it, whether an empty string is sent as it is instead of being refused).
    private static JsonObject PatchBody(string[]? clear, params (string Argument, string ApiName, JsonNode? Value, bool Clearable, bool EmptyAllowed)[] fields)
    {
        var cleared = clear ?? [];
        var clearable = fields.Where(field => field.Clearable).Select(field => field.Argument).ToArray();
        if (cleared.Any(name => !clearable.Contains(name)))
        {
            var unknown = cleared.First(name => !clearable.Contains(name));
            throw KanbanToolErrors.InvalidArgument(
                $"Argument 'clear' names {(unknown is null ? "null" : $"'{unknown}'")}, which cannot be emptied. It may name: {string.Join(", ", clearable)}.");
        }

        var body = new JsonObject();
        foreach (var (argument, apiName, value, _, emptyAllowed) in fields)
        {
            var isCleared = cleared.Contains(argument);
            if (value is not null && isCleared)
            {
                throw KanbanToolErrors.InvalidArgument($"Argument '{argument}' is both passed and named in 'clear'. Do one of the two.");
            }

            if (!emptyAllowed && value is JsonValue text && text.TryGetValue<string>(out var characters) && string.IsNullOrWhiteSpace(characters))
            {
                throw KanbanToolErrors.InvalidArgument(clearable.Contains(argument)
                    ? $"Argument '{argument}' must not be empty. To empty the field, name it in 'clear'."
                    : $"Argument '{argument}' must not be empty.");
            }

            if (value is not null || isCleared)
            {
                body[apiName] = value;
            }
        }

        return body;
    }

    // A write that returns the work item, with API refusals turned into the typed error envelope.
    private async Task<WorkItemDto> SendWorkItemWriteAsync(Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await send(client);
        await KanbanToolErrors.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions)
            ?? throw new InvalidOperationException($"The Kanban API returned no work item for {response.RequestMessage?.RequestUri?.AbsolutePath}.");
    }

    // Write tools call this before touching the API. Tools that do not call it are read tools
    // and stay available to 'read' scope tokens without any change.
    private void EnsureWriteScope()
    {
        var token = httpContextAccessor.HttpContext?.Items[typeof(McpAuthenticatedToken)] as McpAuthenticatedToken;
        if (token is null)
        {
            // PAT auth is required but no token reached this request: fail closed rather than allow writes.
            if (authenticationOptions.Value.RequirePersonalAccessToken)
            {
                throw KanbanToolErrors.ScopeDenied(MissingTokenErrorMessage);
            }

            return;
        }

        if (!token.AllowsWrites)
        {
            throw KanbanToolErrors.ScopeDenied(ReadOnlyScopeErrorMessage);
        }
    }

    private async Task<EpicDto> GetRequiredEpicAsync(HttpClient client, Guid epicId)
    {
        var epic = await client.GetFromJsonAsync<EpicDto>($"/api/epics/{epicId}", JsonOptions);
        return epic ?? throw new InvalidOperationException($"Epic {epicId} was not found.");
    }
}
