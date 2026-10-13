using System.Net;
using System.Text.Json;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// update_work_item, move_work_item and the item shape as an orchestrator sees them: the real MCP server over HTTP,
// a faked API.
public sealed class KanbanToolsWorkItemUpdateTests
{
    // An item without an estimate, as the API sends it.
    private const string ItemJson =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","epicId":null,"epic":null,"title":"Item","description":null,"type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":"api,two words","comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[],"dependents":[],"links":[],"version":7,"label_list":["api","two words"]}""";

    // ItemJson as the tools return it: null properties are left out, except estimate and the four claim fields.
    private const string ItemText =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","title":"Item","type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":"api,two words","comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[],"dependents":[],"links":[],"version":7,"label_list":["api","two words"]}""";

    private const string SearchJson =
        """[{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","title":"Item","type":"Story","status":"Ready","estimate":null,"version":7},{"id":"33333333-3333-3333-3333-333333333333","projectId":"22222222-2222-2222-2222-222222222222","title":"Other","type":"Task","status":"Done","estimate":5,"version":1}]""";

    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EpicId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task The_write_tools_keep_their_arguments_and_gain_optional_snake_case_ones()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        // itemId, title, type, status, priority, epicId, description, estimate and labels are the arguments
        // update_work_item has always had; only itemId is still required.
        Assert.Equal(
            ["clear", "description", "epicId", "estimate", "expected_version", "itemId", "label_list", "labels", "priority", "status", "title", "type"],
            ArgumentNames(tools["update_work_item"]));
        Assert.Equal(["itemId"], RequiredArguments(tools["update_work_item"]));
        Assert.Equal(["expected_version", "itemId", "order", "status"], ArgumentNames(tools["move_work_item"]));
        Assert.Equal(["itemId", "status"], RequiredArguments(tools["move_work_item"]));
        Assert.Equal(
            ["description", "epicId", "estimate", "idempotency_key", "label_list", "labels", "priority", "projectId", "status", "title", "type"],
            ArgumentNames(tools["create_work_item"]));
        Assert.Equal(["projectId", "title", "type"], RequiredArguments(tools["create_work_item"]));
        Assert.Equal(
            ["description", "epicId", "estimate", "idempotency_key", "label_list", "labels", "priority", "status", "title", "type"],
            ArgumentNames(tools["create_epic_work_item"]));
        Assert.Equal(["epicId", "title"], RequiredArguments(tools["create_epic_work_item"]));
        // The read tools take what they always took.
        Assert.Equal(["itemId"], ArgumentNames(tools["get_work_item"]));
        Assert.Equal(["limit", "projectId", "query"], ArgumentNames(tools["search_work_items"]));
    }

    [Fact]
    public async Task Update_with_one_argument_patches_that_field_of_the_item_by_its_id_and_reads_no_board()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        var status = await server.CallAsync("update_work_item", new() { ["itemId"] = ItemId, ["status"] = "InProgress" });
        var estimate = await server.CallAsync("update_work_item", new() { ["itemId"] = ItemId, ["estimate"] = 8 });
        var several = await server.CallAsync("update_work_item", new()
        {
            ["itemId"] = ItemId, ["title"] = "Renamed", ["type"] = "Issue", ["priority"] = "High", ["epicId"] = EpicId, ["description"] = "d", ["labels"] = "a,b", ["expected_version"] = 7
        });

        Assert.All(new[] { status, estimate, several }, result => Assert.NotEqual(true, result.IsError));
        // One request per call: the item is not looked up on any board first.
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Patch, $"/api/items/{ItemId}"), (request.Method, request.Path)));
        Assert.Equal(
            [
                """{"status":"InProgress"}""",
                """{"estimate":8}""",
                $$"""{"title":"Renamed","type":"Issue","priority":"High","epicId":"{{EpicId}}","description":"d","labels":"a,b","expectedVersion":7}"""
            ],
            server.Api.Requests.Select(request => request.Body).ToArray());
        Assert.Equal(ItemText, Text(status));
    }

    [Fact]
    public async Task Update_is_still_callable_with_the_arguments_it_always_required()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        var result = await server.CallAsync("update_work_item", new()
        {
            ["itemId"] = ItemId, ["title"] = "Item", ["type"] = "Story", ["status"] = "Ready", ["priority"] = "Medium"
        });

        Assert.NotEqual(true, result.IsError);
        // As before, the optional fields that were not passed keep their values: they are not sent at all.
        Assert.Equal("""{"title":"Item","type":"Story","status":"Ready","priority":"Medium"}""", Assert.Single(server.Api.Requests).Body);
        Assert.Equal(ItemText, Text(result));
    }

    // Before clear existed, an empty description or labels string emptied the field. It still reaches the API, which
    // stores an empty description and no labels (WorkItemUpdateApiTests).
    [Theory]
    [InlineData("description", "", """{"title":"Item","type":"Story","status":"Ready","priority":"Medium","description":""}""")]
    [InlineData("description", "  ", """{"title":"Item","type":"Story","status":"Ready","priority":"Medium","description":"  "}""")]
    [InlineData("labels", "", """{"title":"Item","type":"Story","status":"Ready","priority":"Medium","labels":""}""")]
    [InlineData("labels", " ", """{"title":"Item","type":"Story","status":"Ready","priority":"Medium","labels":" "}""")]
    [InlineData("labels", " , ", """{"title":"Item","type":"Story","status":"Ready","priority":"Medium","labels":" , "}""")]
    public async Task Update_with_the_arguments_it_always_required_and_an_empty_description_or_labels_still_empties_that_field(string argument, string value, string body)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        var result = await server.CallAsync("update_work_item", new()
        {
            ["itemId"] = ItemId, ["title"] = "Item", ["type"] = "Story", ["status"] = "Ready", ["priority"] = "Medium", [argument] = value
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(body, Assert.Single(server.Api.Requests).Body);
    }

    [Fact]
    public async Task Update_clears_the_fields_named_in_clear_and_sends_a_label_list_as_a_list()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        var cleared = await server.CallAsync("update_work_item", new()
        {
            ["itemId"] = ItemId, ["status"] = "Done", ["clear"] = new[] { "labels", "estimate", "description", "epicId" }
        });
        var listed = await server.CallAsync("update_work_item", new() { ["itemId"] = ItemId, ["label_list"] = new[] { "api", "two words" }, ["clear"] = new[] { "estimate" } });

        Assert.NotEqual(true, cleared.IsError);
        Assert.NotEqual(true, listed.IsError);
        Assert.Equal(
            [
                """{"status":"Done","epicId":null,"description":null,"estimate":null,"labels":null}""",
                """{"estimate":null,"labels":["api","two words"]}"""
            ],
            server.Api.Requests.Select(request => request.Body).ToArray());
    }

    public static TheoryData<Dictionary<string, object?>> RefusedUpdates => new()
    {
        // Not a field of a work item, or one that cannot be empty.
        new Dictionary<string, object?> { ["clear"] = new[] { "label" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "label_list" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "title" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "status" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "type" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "priority" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "expected_version" } },
        new Dictionary<string, object?> { ["clear"] = new string?[] { null } },
        new Dictionary<string, object?> { ["clear"] = new string?[] { "labels", null } },
        // Names are matched exactly.
        new Dictionary<string, object?> { ["clear"] = new[] { "Description" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "epic_id" } },
        // Set and cleared in one call.
        new Dictionary<string, object?> { ["description"] = "d", ["clear"] = new[] { "description" } },
        new Dictionary<string, object?> { ["estimate"] = 3, ["clear"] = new[] { "estimate" } },
        new Dictionary<string, object?> { ["labels"] = "a", ["clear"] = new[] { "labels" } },
        new Dictionary<string, object?> { ["label_list"] = new[] { "a" }, ["clear"] = new[] { "labels" } },
        new Dictionary<string, object?> { ["epicId"] = Guid.Parse("44444444-4444-4444-4444-444444444444"), ["clear"] = new[] { "epicId" } },
        // An empty value is not a way to clear, except a description or labels string (see the test above).
        new Dictionary<string, object?> { ["title"] = "" },
        new Dictionary<string, object?> { ["title"] = " ", ["description"] = "" },
        new Dictionary<string, object?> { ["label_list"] = Array.Empty<string>() },
        new Dictionary<string, object?> { ["label_list"] = new[] { "" } },
        // An empty string is a value: it is not passed together with clear.
        new Dictionary<string, object?> { ["description"] = "", ["clear"] = new[] { "description" } },
        new Dictionary<string, object?> { ["labels"] = "", ["clear"] = new[] { "labels" } },
        // Nothing to change: the version would advance for nothing.
        new Dictionary<string, object?>(),
        new Dictionary<string, object?> { ["expected_version"] = 7 },
        new Dictionary<string, object?> { ["clear"] = Array.Empty<string>() },
        // A label of a list is one label.
        new Dictionary<string, object?> { ["label_list"] = new[] { "a,b" } },
        new Dictionary<string, object?> { ["label_list"] = new[] { "a", " " } },
        new Dictionary<string, object?> { ["label_list"] = new string?[] { "a", null } },
        // The labels are passed one way.
        new Dictionary<string, object?> { ["labels"] = "a", ["label_list"] = new[] { "b" } }
    };

    [Theory]
    [MemberData(nameof(RefusedUpdates))]
    public async Task Update_refuses_what_it_cannot_carry_out_with_invalid_argument_before_any_api_call(Dictionary<string, object?> arguments)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        var result = await server.CallAsync("update_work_item", new(arguments) { ["itemId"] = ItemId });

        AssertError(result, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(server.Api.Requests);
    }

    [Fact]
    public async Task Move_posts_the_expected_version_and_returns_the_item()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        // The call as it was made before the version existed.
        var plain = await server.CallAsync("move_work_item", new() { ["itemId"] = ItemId, ["status"] = "InProgress" });
        var guarded = await server.CallAsync("move_work_item", new() { ["itemId"] = ItemId, ["status"] = "Done", ["order"] = 2, ["expected_version"] = 7 });

        Assert.NotEqual(true, plain.IsError);
        Assert.NotEqual(true, guarded.IsError);
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Post, $"/api/items/{ItemId}/move"), (request.Method, request.Path)));
        Assert.Equal(
            // The status goes out as its number, as it always has; the API reads both.
            ["""{"status":2,"order":0,"expectedVersion":null}""", """{"status":4,"order":2,"expectedVersion":7}"""],
            server.Api.Requests.Select(request => request.Body).ToArray());
        Assert.Equal(ItemText, Text(guarded));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"code":"conflict","message":"Work item ... is at version 8, not 7."}""", KanbanErrorCodes.Conflict)]
    [InlineData(HttpStatusCode.NotFound, """{"code":"not_found","message":"Work item ... was not found."}""", KanbanErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"Argument 'title' ..."}""", KanbanErrorCodes.InvalidArgument)]
    // An answer without a body of the API's own still has a code.
    [InlineData(HttpStatusCode.NotFound, "", KanbanErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, """{"message":"Argument 'title' is required."}""", KanbanErrorCodes.InvalidArgument)]
    public async Task Api_refusal_of_an_update_a_move_or_a_create_becomes_the_typed_error_envelope(HttpStatusCode status, string apiBody, string code)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(status, apiBody));

        var updated = await server.CallAsync("update_work_item", new() { ["itemId"] = ItemId, ["status"] = "Done", ["expected_version"] = 7 });
        var moved = await server.CallAsync("move_work_item", new() { ["itemId"] = ItemId, ["status"] = "Done", ["expected_version"] = 7 });
        var created = await server.CallAsync("create_work_item", new() { ["projectId"] = ProjectId, ["title"] = "x", ["type"] = "Task" });

        AssertError(updated, code);
        AssertError(moved, code);
        AssertError(created, code);
    }

    [Fact]
    public async Task Create_sends_a_label_list_and_is_still_callable_with_comma_separated_labels()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, request =>
            Json(HttpStatusCode.OK, request.Method == HttpMethod.Get
                ? $$"""{"id":"{{EpicId}}","projectId":"{{ProjectId}}","name":"Epic","description":null,"isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00"}"""
                : ItemJson));

        var listed = await server.CallAsync("create_work_item", new() { ["projectId"] = ProjectId, ["title"] = "Item", ["type"] = "Story", ["label_list"] = new[] { "api", "two words" } });
        var joined = await server.CallAsync("create_work_item", new() { ["projectId"] = ProjectId, ["title"] = "Item", ["type"] = "Story", ["labels"] = "api,two words", ["estimate"] = 3 });
        var inEpic = await server.CallAsync("create_epic_work_item", new() { ["epicId"] = EpicId, ["title"] = "Item", ["label_list"] = new[] { "api" } });

        Assert.All(new[] { listed, joined, inEpic }, result => Assert.NotEqual(true, result.IsError));
        var bodies = server.Api.Requests.Where(request => request.Method == HttpMethod.Post).Select(request => JsonDocument.Parse(request.Body).RootElement).ToList();
        Assert.Equal(3, bodies.Count);
        Assert.Equal((JsonValueKind.Null, """["api","two words"]"""), (bodies[0].GetProperty("labels").ValueKind, bodies[0].GetProperty("labelList").GetRawText()));
        Assert.Equal(("api,two words", JsonValueKind.Null, 3), (bodies[1].GetProperty("labels").GetString(), bodies[1].GetProperty("labelList").ValueKind, bodies[1].GetProperty("estimate").GetInt32()));
        Assert.Equal((ProjectId, EpicId, """["api"]"""), (bodies[2].GetProperty("projectId").GetGuid(), bodies[2].GetProperty("epicId").GetGuid(), bodies[2].GetProperty("labelList").GetRawText()));
        Assert.Equal(ItemText, Text(listed));
    }

    [Fact]
    public async Task Every_tool_that_returns_an_item_returns_its_estimate_also_when_null_its_version_and_its_labels_both_ways()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, request => Json(HttpStatusCode.OK, request.Path switch
        {
            "/api/items/search" => SearchJson,
            "/api/items" => request.Query.Contains("limit=", StringComparison.Ordinal) ? $$"""{"items":[{{ItemJson}}],"next_cursor":null}""" : $"[{ItemJson}]",
            "/api/items/by-link" => $"[{ItemJson}]",
            _ when request.Path.StartsWith("/api/projects/", StringComparison.Ordinal) =>
                $$"""{"id":"{{ProjectId}}","name":"P","key":"P","description":null,"isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","items":[{{ItemJson}}]}""",
            _ => ItemJson
        }));

        var texts = new[]
        {
            Text(await server.CallAsync("get_work_item", new() { ["itemId"] = ItemId })),
            Text(await server.CallAsync("list_work_items", [])),
            Text(await server.CallAsync("list_work_items", new() { ["limit"] = 10 })),
            Text(await server.CallAsync("list_epic_work_items", new() { ["epicId"] = EpicId })),
            Text(await server.CallAsync("get_issues", [])),
            Text(await server.CallAsync("get_board", new() { ["projectId"] = ProjectId })),
            Text(await server.CallAsync("find_work_items_by_link", new() { ["url"] = "https://example.test/pr/1" }))
        };
        var found = Text(await server.CallAsync("search_work_items", new() { ["query"] = "item" }));

        Assert.All(texts, text => Assert.Contains(ItemText, text, StringComparison.Ordinal));
        // The search result exactly as the API sent it: its fields as before, then estimate and version.
        Assert.Equal(SearchJson, found);
        Assert.All(server.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Read_scope_token_is_refused_scope_denied_by_update_and_move_before_any_api_call()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, ItemJson));

        var updated = await server.CallAsync("update_work_item", new() { ["itemId"] = ItemId, ["status"] = "Done" });
        // Refused for its scope even when the arguments are malformed as well.
        var malformed = await server.CallAsync("update_work_item", new() { ["itemId"] = ItemId, ["clear"] = new[] { "title" } });
        var moved = await server.CallAsync("move_work_item", new() { ["itemId"] = ItemId, ["status"] = "Done", ["expected_version"] = 7 });

        AssertError(updated, KanbanErrorCodes.ScopeDenied);
        AssertError(malformed, KanbanErrorCodes.ScopeDenied);
        AssertError(moved, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(server.Api.Requests);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
