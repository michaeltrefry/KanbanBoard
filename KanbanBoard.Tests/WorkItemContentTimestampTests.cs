using System.Net.Http.Json;
using KanbanBoard.Shared.Contracts;
using static KanbanBoard.Tests.ProgramDependencyWiringTests;

namespace KanbanBoard.Tests;

// What updatedAtUtc means, through the write endpoints of the API's Program.cs started as the real application:
// it moves when the item's own content changes, and only then.
public sealed class WorkItemContentTimestampTests
{
    [Fact]
    public async Task Moving_an_item_stamps_it_and_leaves_the_siblings_it_displaces_alone()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (a, b, c) = await CreateThreeAsync(client);
        var other = await CreateAsync(client, a.ProjectId, "other column", "Ready");
        var before = await StampsAsync(client);
        await Task.Delay(20);

        // C goes to the top of Backlog, which renumbers A and B; then into Ready, which renumbers the item there.
        var reordered = await PostAsync<WorkItemDto>(client, $"/api/items/{c.Id}/move", new { status = "Backlog", order = 0 });
        var afterReorder = await ItemsAsync(client);
        await Task.Delay(20);
        var moved = await PostAsync<WorkItemDto>(client, $"/api/items/{c.Id}/move", new { status = "Ready", order = 0 });
        var afterMove = await ItemsAsync(client);

        Assert.Equal((1, 2, 0), (afterReorder[a.Id].Order, afterReorder[b.Id].Order, afterReorder[c.Id].Order));
        Assert.Equal(1, afterMove[other.Id].Order);
        Assert.True(reordered.UpdatedAtUtc > before[c.Id]);
        Assert.True(moved.UpdatedAtUtc > reordered.UpdatedAtUtc);
        Assert.Equal(moved.UpdatedAtUtc, afterMove[c.Id].UpdatedAtUtc);
        Assert.Equal(before[a.Id], afterMove[a.Id].UpdatedAtUtc);
        Assert.Equal(before[b.Id], afterMove[b.Id].UpdatedAtUtc);
        Assert.Equal(before[other.Id], afterMove[other.Id].UpdatedAtUtc);
    }

    [Fact]
    public async Task A_comment_stamps_its_item_and_updated_since_then_finds_that_item_only()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (a, b, _) = await CreateThreeAsync(client);
        var before = await StampsAsync(client);
        await Task.Delay(20);
        var since = DateTimeOffset.UtcNow;
        await Task.Delay(20);

        var commented = await PostAsync<WorkItemDto>(client, $"/api/items/{a.Id}/comments", new { author = "tester", body = "note" });
        var after = await ItemsAsync(client);
        var changed = await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items?updatedSince={Uri.EscapeDataString(since.ToString("O"))}", Json);

        Assert.True(commented.UpdatedAtUtc > since);
        Assert.Equal(commented.UpdatedAtUtc, after[a.Id].UpdatedAtUtc);
        Assert.Equal(Assert.Single(commented.Comments).CreatedAtUtc, commented.UpdatedAtUtc);
        Assert.Equal(before[b.Id], after[b.Id].UpdatedAtUtc);
        Assert.Equal(a.Id, Assert.Single(changed!).Id);
        // The stamp a write returned selects the item it was written to.
        var fromStamp = await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items?updatedSince={Uri.EscapeDataString(commented.UpdatedAtUtc.ToString("O"))}", Json);
        Assert.Equal(a.Id, Assert.Single(fromStamp!).Id);
    }

    [Fact]
    public async Task A_field_change_stamps_the_item_and_no_other()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (a, b, _) = await CreateThreeAsync(client);
        var before = await StampsAsync(client);
        await Task.Delay(20);

        var updated = await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{a.Id}", new { labels = "factory" });
        var after = await ItemsAsync(client);

        Assert.Equal("factory", updated.Labels);
        Assert.True(after[a.Id].UpdatedAtUtc > before[a.Id]);
        Assert.Equal(before[b.Id], after[b.Id].UpdatedAtUtc);
    }

    private static async Task<(WorkItemDto A, WorkItemDto B, WorkItemDto C)> CreateThreeAsync(HttpClient client)
    {
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "STAMP" });
        return (
            await CreateAsync(client, project.Id, "A", "Backlog"),
            await CreateAsync(client, project.Id, "B", "Backlog"),
            await CreateAsync(client, project.Id, "C", "Backlog"));
    }

    private static Task<WorkItemDto> CreateAsync(HttpClient client, Guid projectId, string title, string status) =>
        PostAsync<WorkItemDto>(client, "/api/items", new { projectId, title, type = "Task", status, priority = "Medium" });

    private static async Task<Dictionary<Guid, WorkItemDto>> ItemsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<WorkItemDto>>("/api/items", Json))!.ToDictionary(item => item.Id);

    private static async Task<Dictionary<Guid, DateTimeOffset>> StampsAsync(HttpClient client) =>
        (await ItemsAsync(client)).ToDictionary(entry => entry.Key, entry => entry.Value.UpdatedAtUtc);
}
