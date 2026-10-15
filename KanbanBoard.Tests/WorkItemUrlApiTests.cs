using System.Net.Http.Json;
using System.Text.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Shared.Contracts;
using static KanbanBoard.Tests.ProgramDependencyWiringTests;

namespace KanbanBoard.Tests;

// The url of a story's page on every answer that carries a work item, made from App:PublicBaseUrl and never from the
// request. The real application, as in ProgramDependencyWiringTests.
public sealed class WorkItemUrlApiTests
{
    private const string BaseUrl = "https://kanban.example.com";

    [Fact]
    public async Task With_a_public_base_url_every_work_item_answer_carries_the_url_of_its_page()
    {
        // The trailing slash of the configured address does not make a double slash.
        using var api = new ApiApplication { PublicBaseUrl = BaseUrl + "/" };
        using var client = api.CreateClient();
        using var caller = api.CreateCallerClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "URL" });
        var a = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Alpha needle", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Beta", type = "Task", status = "Backlog", priority = "Medium" });
        var aUrl = $"{BaseUrl}/stories/{a.Id}";
        var bUrl = $"{BaseUrl}/stories/{b.Id}";

        Assert.Equal(aUrl, a.Url);
        Assert.Equal(aUrl, (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{a.Id}", Json))!.Url);
        var listed = (await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items?projectId={project.Id}", Json))!;
        Assert.Equal([aUrl, bUrl], listed.OrderBy(item => item.Title).Select(item => item.Url));
        var page = (await client.GetFromJsonAsync<WorkItemPageDto>($"/api/items?projectId={project.Id}&limit=5", Json))!;
        Assert.All(page.Items, item => Assert.Equal($"{BaseUrl}/stories/{item.Id}", item.Url));
        var found = Assert.Single((await client.GetFromJsonAsync<List<WorkItemSearchResultDto>>("/api/items/search?query=needle", Json))!);
        Assert.Equal(aUrl, found.Url);
        var board = (await client.GetFromJsonAsync<ProjectBoardDto>($"/api/projects/{project.Id}", Json))!;
        Assert.All(board.Items, item => Assert.Equal($"{BaseUrl}/stories/{item.Id}", item.Url));

        // The answers of the writes that return the item.
        Assert.Equal(bUrl, (await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{b.Id}", new { description = "changed" })).Url);
        Assert.Equal(bUrl, (await SendAsync<WorkItemDto>(client, HttpMethod.Patch, $"/api/items/{b.Id}", new { description = "again" })).Url);
        Assert.Equal(bUrl, (await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/move", new { status = "InProgress", order = 0 })).Url);
        Assert.Equal(bUrl, (await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/comments", new { author = "tester", body = "note" })).Url);
        Assert.Equal(bUrl, (await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/dependencies", new AddWorkItemDependencyRequest(a.Id))).Url);
        Assert.Equal(bUrl, (await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/links", new AddWorkItemLinkRequest("pr", "https://github.com/acme/app/pull/1"))).Url);
        Assert.Equal(bUrl, Assert.Single((await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items/by-link?url={Uri.EscapeDataString("https://github.com/acme/app/pull/1")}", Json))!).Url);
        Assert.Equal(bUrl, (await PostAsync<WorkItemDto>(caller, $"/api/items/{b.Id}/claim", new ClaimWorkItemRequest("run-1", 60))).Url);
    }

    [Fact]
    public async Task Without_a_public_base_url_the_url_is_written_as_null()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "NOURL" });
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "needle", type = "Task", status = "Backlog", priority = "Medium" });

        Assert.Null(item.Url);
        AssertNullUrl((await GetJsonAsync(client, $"/api/items/{item.Id}")).RootElement);
        AssertNullUrl((await GetJsonAsync(client, "/api/items")).RootElement[0]);
        AssertNullUrl((await GetJsonAsync(client, "/api/items/search?query=needle")).RootElement[0]);
        AssertNullUrl((await GetJsonAsync(client, $"/api/projects/{project.Id}")).RootElement.GetProperty("items")[0]);
    }

    [Fact]
    public void A_public_base_url_that_is_not_an_absolute_http_url_is_refused_at_startup()
    {
        using var api = new ApiApplication { PublicBaseUrl = "kanban.example.com" };

        var exception = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains("App:PublicBaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("https://kanban.example.com", "https://kanban.example.com/stories/")]
    [InlineData("https://kanban.example.com/", "https://kanban.example.com/stories/")]
    [InlineData(" https://kanban.example.com// ", "https://kanban.example.com/stories/")]
    [InlineData("http://example.com/kanban/", "http://example.com/kanban/stories/")]
    public void The_story_url_is_the_base_without_trailing_slashes_then_stories_and_the_id(string? baseUrl, string? expectedPrefix)
    {
        var id = Guid.NewGuid();
        var options = new AppOptions { PublicBaseUrl = baseUrl };

        Assert.Equal(expectedPrefix is null ? null : expectedPrefix + id, options.StoryUrl(id));
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("kanban.example.com")]
    [InlineData("/kanban")]
    [InlineData("ftp://kanban.example.com")]
    [InlineData("https://kanban.example.com/?a=1")]
    [InlineData("https://kanban.example.com/#top")]
    public void Validate_refuses_a_base_that_is_not_an_absolute_http_url(string baseUrl) =>
        Assert.NotEmpty(new AppOptions { PublicBaseUrl = baseUrl }.Validate());

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path) =>
        JsonDocument.Parse(await client.GetStringAsync(path));

    private static void AssertNullUrl(JsonElement item)
    {
        Assert.True(item.TryGetProperty("url", out var url), "The url property is missing.");
        Assert.Equal(JsonValueKind.Null, url.ValueKind);
    }
}
