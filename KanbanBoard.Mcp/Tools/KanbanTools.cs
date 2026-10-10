using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Mcp.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
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
        "This personal access token has the 'read' scope and cannot create, update, move, comment on, or delete data. " +
        "Create a token with the 'full' scope in Kanban settings to use write tools.";

    public sealed record DeleteResultDto(Guid Id, string EntityType, bool Deleted);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool, Description("List all projects in the kanban board.")]
    public async Task<IReadOnlyList<ProjectSummaryDto>> ListProjects(
        [Description("Whether to include archived projects")] bool includeArchived = false)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        return await client.GetFromJsonAsync<IReadOnlyList<ProjectSummaryDto>>($"/api/projects?includeArchived={includeArchived}", JsonOptions)
            ?? [];
    }

    [McpServerTool, Description("Create a new project.")]
    public async Task<ProjectSummaryDto?> CreateProject(
        [Description("Project name")] string name,
        [Description("Short uppercase key")] string key,
        [Description("Optional description")] string? description = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, key, description, false));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProjectSummaryDto>(JsonOptions);
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

    [McpServerTool, Description("List work items, optionally filtered by project, epic, type, and status. Closed items stay persisted but are hidden unless status=Closed is requested.")]
    public async Task<IReadOnlyList<WorkItemDto>> ListWorkItems(
        [Description("Optional project id")] Guid? projectId = null,
        [Description("Optional epic id")] Guid? epicId = null,
        [Description("Optional work item type: Story, Issue, or Task")] WorkItemType? type = null,
        [Description("Optional status filter")] WorkItemStatus? status = null,
        [Description("Whether to include items that belong to archived epics")] bool includeArchivedEpics = false)
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

        var path = parameters.Count == 0
            ? "/api/items"
            : $"/api/items?{string.Join('&', parameters)}";

        return await client.GetFromJsonAsync<IReadOnlyList<WorkItemDto>>(path, JsonOptions) ?? [];
    }

    [McpServerTool, Description("Fetch a single work item by id, including its description, status, and comments.")]
    public async Task<WorkItemDto> GetWorkItem(
        [Description("Work item id")] Guid itemId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        using var response = await client.GetAsync($"/api/items/{itemId}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new ModelContextProtocol.McpException($"Work item {itemId} was not found.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions)
            ?? throw new ModelContextProtocol.McpException($"Work item {itemId} was not found.");
    }

    [McpServerTool, Description("Search work items whose title or description contains every word in the query (case-insensitive). Returns ids, titles, types, and statuses only (no bodies), including closed items, capped at a page size. Use GetWorkItem for full details.")]
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
    public Task<IReadOnlyList<WorkItemDto>> ListEpicWorkItems(
        [Description("Epic id")] Guid epicId,
        [Description("Optional work item type: Story, Issue, or Task")] WorkItemType? type = null,
        [Description("Optional status filter")] WorkItemStatus? status = null,
        [Description("Whether to include items when the epic is archived")] bool includeArchivedEpics = false) =>
        ListWorkItems(epicId: epicId, type: type, status: status, includeArchivedEpics: includeArchivedEpics);

    [McpServerTool, Description("Fetch issue work items, optionally filtered by project and status.")]
    public async Task<IReadOnlyList<WorkItemDto>> GetIssues(
        [Description("Optional project id")] Guid? projectId = null,
        [Description("Optional status filter")] WorkItemStatus? status = null)
    {
        return await ListWorkItems(projectId: projectId, type: WorkItemType.Issue, status: status);
    }

    [McpServerTool, Description("Add a comment to any work item. When an LLM adds a comment, set author to the assistant name such as codex or claude.")]
    public async Task<WorkItemDto?> AddWorkItemComment(
        [Description("Work item id")] Guid itemId,
        [Description("Comment author. For LLM-authored comments, use your assistant name like codex or claude.")] string author,
        [Description("Comment text")] string body)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync($"/api/items/{itemId}/comments", new CreateWorkItemCommentRequest(author, body));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Add a comment to an issue. When an LLM adds a comment, set author to the assistant name such as codex or claude.")]
    public Task<WorkItemDto?> AddIssueComment(
        [Description("Issue work item id")] Guid itemId,
        [Description("Comment author. For LLM-authored comments, use your assistant name like codex or claude.")] string author,
        [Description("Comment text")] string body) =>
        AddWorkItemComment(itemId, author, body);

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
        [Description("Optional comma-separated labels")] string? labels = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync("/api/items", new CreateWorkItemRequest(
            projectId,
            epicId,
            title,
            description,
            type,
            status,
            priority,
            estimate,
            labels));

        response.EnsureSuccessStatusCode();
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
        [Description("Optional comma-separated labels")] string? labels = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var epic = await GetRequiredEpicAsync(client, epicId);

        var response = await client.PostAsJsonAsync("/api/items", new CreateWorkItemRequest(
            epic.ProjectId,
            epicId,
            title,
            description,
            type,
            status,
            priority,
            estimate,
            labels));

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Move a work item to a new column and order.")]
    public async Task<WorkItemDto?> MoveWorkItem(
        [Description("Work item id")] Guid itemId,
        [Description("Target status")] WorkItemStatus status,
        [Description("Zero-based order in the target column")] int order = 0)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync($"/api/items/{itemId}/move", new MoveWorkItemRequest(status, order));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions);
    }

    [McpServerTool, Description("Update a work item.")]
    public async Task<WorkItemDto?> UpdateWorkItem(
        [Description("Work item id")] Guid itemId,
        [Description("Title")] string title,
        [Description("Type")] WorkItemType type,
        [Description("Status")] WorkItemStatus status,
        [Description("Priority")] WorkItemPriority priority,
        [Description("Optional epic id")] Guid? epicId = null,
        [Description("Optional description")] string? description = null,
        [Description("Optional estimate")] int? estimate = null,
        [Description("Optional comma-separated labels")] string? labels = null)
    {
        EnsureWriteScope();
        var client = httpClientFactory.CreateClient("kanban-api");
        var board = await FindBoardForItemAsync(client, itemId);
        var item = board?.Items.FirstOrDefault(candidate => candidate.Id == itemId);

        if (item is null)
        {
            throw new InvalidOperationException($"Work item {itemId} was not found.");
        }

        // MCP optional parameters collapse "omitted" and "null" into the same value,
        // so we preserve the current optional fields unless the caller provided a value.
        var response = await client.PutAsJsonAsync($"/api/items/{itemId}", new UpdateWorkItemRequest(
            epicId ?? item.EpicId,
            title,
            description ?? item.Description,
            type,
            status,
            priority,
            estimate ?? item.Estimate,
            labels ?? item.Labels));

        response.EnsureSuccessStatusCode();
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
                throw new McpException(MissingTokenErrorMessage);
            }

            return;
        }

        if (!token.AllowsWrites)
        {
            throw new McpException(ReadOnlyScopeErrorMessage);
        }
    }

    private async Task<EpicDto> GetRequiredEpicAsync(HttpClient client, Guid epicId)
    {
        var epic = await client.GetFromJsonAsync<EpicDto>($"/api/epics/{epicId}", JsonOptions);
        return epic ?? throw new InvalidOperationException($"Epic {epicId} was not found.");
    }

    private async Task<ProjectBoardDto?> FindBoardForItemAsync(HttpClient client, Guid itemId)
    {
        var projects = await client.GetFromJsonAsync<IReadOnlyList<ProjectSummaryDto>>("/api/projects", JsonOptions) ?? [];

        foreach (var project in projects)
        {
            var board = await client.GetFromJsonAsync<ProjectBoardDto>($"/api/projects/{project.Id}", JsonOptions);
            if (board?.Items.Any(item => item.Id == itemId) == true)
            {
                return board;
            }
        }

        return null;
    }
}
