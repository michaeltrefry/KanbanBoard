using System.Net;
using System.Text.Json;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// The dependency tools and read fields as an orchestrator sees them: the real MCP server over HTTP, a faked API.
public sealed class KanbanToolsDependencyTests
{
    private const string BlockedItemJson =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","epicId":null,"epic":null,"title":"B","description":null,"type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":null,"comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[{"id":"44444444-4444-4444-4444-444444444444","title":"A","status":"Done"}],"dependents":[{"id":"55555555-5555-5555-5555-555555555555","title":"C","status":"Backlog"}]}""";

    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BlockerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Dependency_tools_and_the_unblocked_only_argument_are_listed_in_snake_case()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, BlockedItemJson));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        Assert.Equal(["blocked_by_id", "id"], ArgumentNames(tools["add_work_item_dependency"]));
        Assert.Equal(["blocked_by_id", "id"], RequiredArguments(tools["add_work_item_dependency"]));
        Assert.Equal(["blocked_by_id", "id"], ArgumentNames(tools["remove_work_item_dependency"]));
        Assert.Equal(["blocked_by_id", "id"], RequiredArguments(tools["remove_work_item_dependency"]));
        // The list tools keep their arguments and gain one optional argument.
        Assert.Contains("unblocked_only", ArgumentNames(tools["list_work_items"]));
        Assert.Equal(["epicId", "includeArchivedEpics", "status", "type", "unblocked_only"], ArgumentNames(tools["list_epic_work_items"]));
        Assert.Equal(["projectId", "status", "unblocked_only"], ArgumentNames(tools["get_issues"]));
        Assert.DoesNotContain("unblocked_only", OptionalRequired(tools["list_work_items"]));
        Assert.DoesNotContain("unblocked_only", OptionalRequired(tools["list_epic_work_items"]));
        Assert.DoesNotContain("unblocked_only", OptionalRequired(tools["get_issues"]));
    }

    [Fact]
    public async Task Add_posts_the_blocker_id_and_returns_the_item_with_its_dependencies()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, BlockedItemJson));

        var result = await server.CallAsync("add_work_item_dependency", new() { ["id"] = ItemId, ["blocked_by_id"] = BlockerId });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/items/{ItemId}/dependencies"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(BlockerId, body.RootElement.GetProperty("blockedByItemId").GetGuid());
        Assert.Single(body.RootElement.EnumerateObject());
        AssertDependencyFields(result);
    }

    [Fact]
    public async Task Remove_deletes_the_edge_route_and_returns_the_item()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, BlockedItemJson));

        var result = await server.CallAsync("remove_work_item_dependency", new() { ["id"] = ItemId, ["blocked_by_id"] = BlockerId });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Delete, $"/api/items/{ItemId}/dependencies/{BlockerId}"), (request.Method, request.Path));
        AssertDependencyFields(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"code":"cycle","message":"would form a cycle"}""", KanbanErrorCodes.Cycle)]
    [InlineData(HttpStatusCode.NotFound, """{"code":"not_found","message":"no such item"}""", KanbanErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"busy","message":"Nothing was changed; try again."}""", KanbanErrorCodes.Busy)]
    public async Task Api_refusal_of_a_dependency_becomes_the_typed_error_envelope(HttpStatusCode status, string apiBody, string code)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(status, apiBody));

        var added = await server.CallAsync("add_work_item_dependency", new() { ["id"] = ItemId, ["blocked_by_id"] = BlockerId });
        var removed = await server.CallAsync("remove_work_item_dependency", new() { ["id"] = ItemId, ["blocked_by_id"] = BlockerId });

        AssertError(added, code);
        AssertError(removed, code);
    }

    [Fact]
    public async Task A_503_the_api_did_not_write_is_a_fault_without_a_code()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.ServiceUnavailable, "<html>proxy</html>"));

        var result = await server.CallAsync("add_work_item_dependency", new() { ["id"] = ItemId, ["blocked_by_id"] = BlockerId });

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.DoesNotContain(KanbanErrorCodes.Busy, string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add_work_item_dependency")]
    [InlineData("remove_work_item_dependency")]
    public async Task Read_scope_token_is_refused_scope_denied_before_any_api_call(string tool)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, BlockedItemJson));

        var result = await server.CallAsync(tool, new() { ["id"] = ItemId, ["blocked_by_id"] = BlockerId });

        AssertError(result, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(server.Api.Requests);
    }

    [Theory]
    [InlineData("get_work_item", "itemId")]
    [InlineData("list_work_items", null)]
    [InlineData("list_epic_work_items", "epicId")]
    [InlineData("get_issues", null)]
    [InlineData("get_board", "projectId")]
    public async Task Read_tools_return_blockers_and_dependents_with_status_to_a_read_scope_token(string tool, string? idArgument)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, request =>
            Json(HttpStatusCode.OK, request.Path.StartsWith("/api/projects/", StringComparison.Ordinal)
                ? $$"""{"id":"22222222-2222-2222-2222-222222222222","name":"P","key":"P","description":null,"isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","items":[{{BlockedItemJson}}]}"""
                : request.Path == "/api/items" ? $"[{BlockedItemJson}]" : BlockedItemJson));

        var result = await server.CallAsync(tool, idArgument is null ? [] : new() { [idArgument] = ItemId });

        Assert.NotEqual(true, result.IsError);
        Assert.All(server.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        AssertDependencyFields(result);
    }

    [Theory]
    [InlineData("list_work_items", null)]
    [InlineData("list_epic_work_items", "epicId")]
    [InlineData("get_issues", null)]
    public async Task List_tools_forward_unblocked_only_only_when_asked(string tool, string? idArgument)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, $"[{BlockedItemJson}]"));
        var arguments = idArgument is null ? new Dictionary<string, object?>() : new() { [idArgument] = ItemId };

        await server.CallAsync(tool, new(arguments));
        await server.CallAsync(tool, new(arguments) { ["unblocked_only"] = false });
        var filtered = await server.CallAsync(tool, new(arguments) { ["unblocked_only"] = true });

        Assert.NotEqual(true, filtered.IsError);
        Assert.Equal(3, server.Api.Requests.Count);
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Get, "/api/items"), (request.Method, request.Path)));
        Assert.DoesNotContain("unblockedOnly", server.Api.Requests[0].Query, StringComparison.Ordinal);
        Assert.DoesNotContain("unblockedOnly", server.Api.Requests[1].Query, StringComparison.Ordinal);
        Assert.Contains("unblockedOnly=true", server.Api.Requests[2].Query.TrimStart('?').Split('&'));
    }

    private static string[] OptionalRequired(ModelContextProtocol.Client.McpClientTool tool) =>
        tool.JsonSchema.TryGetProperty("required", out _) ? RequiredArguments(tool) : [];

    private static void AssertDependencyFields(CallToolResult result)
    {
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("\"blockers\":[{\"id\":\"44444444-4444-4444-4444-444444444444\",\"title\":\"A\",\"status\":\"Done\"}]", text, StringComparison.Ordinal);
        Assert.Contains("\"dependents\":[{\"id\":\"55555555-5555-5555-5555-555555555555\",\"title\":\"C\",\"status\":\"Backlog\"}]", text, StringComparison.Ordinal);
    }
}
