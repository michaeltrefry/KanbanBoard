using System.Net;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// list_changes as an orchestrator sees it: the real MCP server over HTTP, a faked API.
public sealed class KanbanToolsChangeFeedTests
{
    private const string EntryJson =
        """{"sequence":42,"item_id":"11111111-1111-1111-1111-111111111111","project_id":"22222222-2222-2222-2222-222222222222","kind":"item_moved","actor":{"id":"33333333-3333-3333-3333-333333333333","name":"Factory"},"changes":[{"field":"status","old":"Backlog","new":"InProgress"},{"field":"estimate","old":null,"new":3},{"field":"comment","old":null,"new":{"id":"77777777-7777-7777-7777-777777777777","author":"Factory","body":"shipped"}}],"created_at_utc":"2026-10-12T12:00:00+00:00"}""";

    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task List_changes_is_listed_in_snake_case_with_optional_arguments_only()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, """{"items":[],"next_cursor":"aC4w"}"""));

        var tool = (await server.Client.ListToolsAsync()).Single(candidate => candidate.Name == "list_changes");

        Assert.Equal(["cursor", "from_start", "limit", "project_id"], ArgumentNames(tool));
        Assert.False(tool.JsonSchema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0);
        // The kinds are part of the contract a caller reads.
        Assert.All(WorkItemChangeKinds.All, kind => Assert.Contains(kind, tool.Description, StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_changes_passes_its_arguments_on_and_returns_the_entries_and_the_cursor_as_the_api_sent_them()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.OK, $$"""{"items":[{{EntryJson}}],"next_cursor":"aC40Mg"}"""));

        var head = await server.CallAsync("list_changes", new());
        var page = await server.CallAsync("list_changes", new() { ["cursor"] = "aC4+MQ", ["project_id"] = ProjectId, ["limit"] = 25 });
        var fromStart = await server.CallAsync("list_changes", new() { ["from_start"] = true });

        Assert.All(new[] { head, page, fromStart }, result => Assert.NotEqual(true, result.IsError));
        // A read tool: a read-scope token reaches the API, with GETs only.
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Get, "/api/items/changes"), (request.Method, request.Path)));
        Assert.Equal(
            [string.Empty, $"?cursor=aC4%2BMQ&projectId={ProjectId}&limit=25", "?fromStart=true"],
            server.Api.Requests.Select(request => request.Query).ToArray());
        // Old and new values come through as they are, null included.
        Assert.Equal($$"""{"items":[{{EntryJson}}],"next_cursor":"aC40Mg"}""", Text(page));
    }

    [Fact]
    public async Task List_changes_returns_the_cursor_also_when_there_is_no_entry_and_leaves_out_an_actor_there_is_none_of()
    {
        var withoutActor = EntryJson.Replace("""{"id":"33333333-3333-3333-3333-333333333333","name":"Factory"}""", "null", StringComparison.Ordinal);
        var bodies = new Queue<string>(["""{"items":[],"next_cursor":"aC4w"}""", $$"""{"items":[{{withoutActor}}],"next_cursor":"aC40Mg"}"""]);
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, bodies.Dequeue()));

        var empty = await server.CallAsync("list_changes", new());
        var anonymous = await server.CallAsync("list_changes", new() { ["cursor"] = "aC4w" });

        Assert.Equal("""{"items":[],"next_cursor":"aC4w"}""", Text(empty));
        Assert.DoesNotContain("\"actor\"", Text(anonymous), StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"item_moved\",\"changes\":[", Text(anonymous), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_refusal_of_list_changes_becomes_the_typed_error_envelope()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"Argument 'cursor' is not a cursor returned by this tool. Pass next_cursor from the previous call unchanged."}"""));

        var result = await server.CallAsync("list_changes", new() { ["cursor"] = "x" });

        Assert.Contains("next_cursor", AssertError(result, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
    }

    // Every write tool that reaches the change feed, or any other refusal of the API that carries a code.
    public static TheoryData<string, Dictionary<string, object?>> WriteTools => new()
    {
        { "update_epic", new() { ["epicId"] = ProjectId, ["name"] = "Epic", ["isArchived"] = true } },
        { "delete_project", new() { ["projectId"] = ProjectId } },
        { "delete_epic", new() { ["epicId"] = ProjectId } },
        { "delete_work_item", new() { ["itemId"] = ProjectId } },
        { "create_epic", new() { ["projectId"] = ProjectId, ["name"] = "Epic" } },
        { "create_epic_document", new() { ["epicId"] = ProjectId, ["title"] = "Title", ["body"] = "Body" } },
        { "update_epic_document", new() { ["documentId"] = ProjectId, ["title"] = "Title", ["body"] = "Body" } }
    };

    [Theory]
    [MemberData(nameof(WriteTools))]
    public async Task A_write_the_api_refuses_busy_becomes_the_typed_error_envelope(string tool, Dictionary<string, object?> arguments)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ =>
            Json(HttpStatusCode.ServiceUnavailable, """{"code":"busy","message":"Other work item changes were being recorded for 10 s. Nothing was changed; try again."}"""));

        var result = await server.CallAsync(tool, arguments);

        Assert.Contains("Nothing was changed", AssertError(result, KanbanErrorCodes.Busy), StringComparison.Ordinal);
        Assert.Single(server.Api.Requests);
    }

    [Fact]
    public async Task List_changes_says_what_a_follower_cannot_see_from_the_entries()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, """{"items":[],"next_cursor":"aC4w"}"""));

        var description = (await server.Client.ListToolsAsync()).Single(candidate => candidate.Name == "list_changes").Description;

        Assert.Contains("16384 characters", description, StringComparison.Ordinal);
        Assert.Contains("old_truncated", description, StringComparison.Ordinal);
        Assert.Contains("new_truncated", description, StringComparison.Ordinal);
        Assert.Contains("A project_id no project has is not refused", description, StringComparison.Ordinal);
        Assert.Contains("ahead of the newest entry", description, StringComparison.Ordinal);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
