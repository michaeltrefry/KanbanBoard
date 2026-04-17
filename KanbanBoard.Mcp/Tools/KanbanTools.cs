using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Server;

namespace KanbanBoard.Mcp.Tools;

[McpServerToolType]
public sealed class KanbanTools(IHttpClientFactory httpClientFactory)
{
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
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, key, description, false));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProjectSummaryDto>(JsonOptions);
    }

    [McpServerTool, Description("Fetch a project board, optionally filtered to a single status.")]
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
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PutAsJsonAsync($"/api/epics/{epicId}", new UpdateEpicRequest(name, description, isArchived));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EpicDto>(JsonOptions);
    }

    [McpServerTool, Description("Delete a project.")]
    public async Task DeleteProject(
        [Description("Project id")] Guid projectId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.DeleteAsync($"/api/projects/{projectId}");
        response.EnsureSuccessStatusCode();
    }

    [McpServerTool, Description("Delete an epic.")]
    public async Task DeleteEpic(
        [Description("Epic id")] Guid epicId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.DeleteAsync($"/api/epics/{epicId}");
        response.EnsureSuccessStatusCode();
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
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PutAsJsonAsync($"/api/epic-documents/{documentId}", new UpdateEpicDocumentRequest(title, body));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EpicDocumentDto>(JsonOptions);
    }

    [McpServerTool, Description("Fetch issue work items, optionally filtered by project and status.")]
    public async Task<IReadOnlyList<WorkItemDto>> GetIssues(
        [Description("Optional project id")] Guid? projectId = null,
        [Description("Optional status filter")] WorkItemStatus? status = null)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        var parameters = new List<string> { "type=Issue" };

        if (projectId is not null)
        {
            parameters.Add($"projectId={projectId}");
        }

        if (status is not null)
        {
            parameters.Add($"status={status}");
        }

        var path = $"/api/items?{string.Join('&', parameters)}";
        return await client.GetFromJsonAsync<IReadOnlyList<WorkItemDto>>(path, JsonOptions) ?? [];
    }

    [McpServerTool, Description("Create a work item in a project.")]
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

    [McpServerTool, Description("Move a work item to a new column and order.")]
    public async Task<WorkItemDto?> MoveWorkItem(
        [Description("Work item id")] Guid itemId,
        [Description("Target status")] WorkItemStatus status,
        [Description("Zero-based order in the target column")] int order = 0)
    {
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
        var client = httpClientFactory.CreateClient("kanban-api");
        var board = await FindBoardForItemAsync(client, itemId);
        var item = board?.Items.FirstOrDefault(candidate => candidate.Id == itemId);

        if (item is null)
        {
            throw new InvalidOperationException($"Work item {itemId} was not found.");
        }

        var response = await client.PutAsJsonAsync($"/api/items/{itemId}", new UpdateWorkItemRequest(
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

    [McpServerTool, Description("Delete a work item.")]
    public async Task DeleteWorkItem(
        [Description("Work item id")] Guid itemId)
    {
        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.DeleteAsync($"/api/items/{itemId}");
        response.EnsureSuccessStatusCode();
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
