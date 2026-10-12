using System.Net;
using System.Text.Json;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// list_work_items as an orchestrator sees it: the real MCP server over HTTP, a faked API.
public sealed class KanbanToolsListTests
{
    private const string ItemJson =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","epicId":null,"epic":null,"title":"Item","description":null,"type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":"factory","comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[],"dependents":[],"links":[]}""";

    // ItemJson as the tools return it: in an array, with the null properties left out.
    private const string ExpectedArrayText =
        """[{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","title":"Item","type":"Story","status":"Ready","priority":"Medium","order":0,"labels":"factory","comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","blockers":[],"dependents":[],"links":[]}]""";

    private static readonly string[] OriginalArguments = ["epicId", "includeArchivedEpics", "projectId", "status", "type", "unblocked_only"];

    [Fact]
    public async Task List_work_items_keeps_its_arguments_and_gains_optional_snake_case_ones()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, $"[{ItemJson}]"));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        string[] added = ["claim_state", "cursor", "include_comments", "label", "limit", "updated_since"];
        Assert.Equal(OriginalArguments.Concat(added).Order(), ArgumentNames(tools["list_work_items"]));
        Assert.False(tools["list_work_items"].JsonSchema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0);
        // The other list tools are as they were.
        Assert.Equal(["epicId", "includeArchivedEpics", "status", "type", "unblocked_only"], ArgumentNames(tools["list_epic_work_items"]));
        Assert.Equal(["projectId", "status", "unblocked_only"], ArgumentNames(tools["get_issues"]));
    }

    [Fact]
    public async Task With_only_the_original_arguments_the_request_and_the_array_result_are_unchanged()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, $"[{ItemJson}]"));
        var projectId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var bare = await server.CallAsync("list_work_items", []);
        var filtered = await server.CallAsync("list_work_items", new()
        {
            ["projectId"] = projectId, ["type"] = "Story", ["status"] = "Ready", ["includeArchivedEpics"] = true, ["unblocked_only"] = true
        });

        Assert.Equal(string.Empty, server.Api.Requests[0].Query);
        Assert.Equal($"?projectId={projectId}&type=Story&status=Ready&includeArchivedEpics=true&unblockedOnly=true", server.Api.Requests[1].Query);
        // The array result, byte for byte: the API answer with its null properties left out.
        Assert.Equal(ExpectedArrayText, Text(bare));
        Assert.Equal(ExpectedArrayText, Text(filtered));
        // get_issues reads through the same code and returns the same text for the same API answer.
        Assert.Equal(ExpectedArrayText, Text(await server.CallAsync("get_issues", [])));
        Assert.NotEqual(true, bare.IsError);
    }

    [Fact]
    public async Task New_arguments_are_forwarded_escaped_and_a_page_comes_back_as_items_and_next_cursor()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.OK, $$"""{"items":[{{ItemJson}}],"next_cursor":"bmV4dA"}"""));

        var result = await server.CallAsync("list_work_items", new()
        {
            ["status"] = "Ready",
            ["label"] = "needs review",
            ["updated_since"] = "2026-10-12T12:00:00+00:00",
            ["claim_state"] = "unclaimed",
            ["limit"] = 25,
            ["cursor"] = "cHJldg",
            ["include_comments"] = false
        });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Get, "/api/items"), (request.Method, request.Path));
        Assert.Equal(
            [
                "status=Ready", "label=needs%20review", "updatedSince=2026-10-12T12%3A00%3A00%2B00%3A00", "claimState=unclaimed",
                "limit=25", "cursor=cHJldg", "includeComments=false"
            ],
            request.Query.TrimStart('?').Split('&'));
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal(["items", "next_cursor"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("bmV4dA", json.RootElement.GetProperty("next_cursor").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", json.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_last_page_has_no_next_cursor()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.OK, """{"items":[],"next_cursor":null}"""));

        var result = await server.CallAsync("list_work_items", new() { ["limit"] = 10 });

        // The MCP server leaves null fields out of every result, so the last page is the one without next_cursor.
        Assert.Equal("""{"items":[]}""", Text(result));
    }

    [Fact]
    public async Task Filters_without_limit_or_cursor_still_return_an_array()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, $"[{ItemJson}]"));

        var result = await server.CallAsync("list_work_items", new() { ["label"] = "factory", ["claim_state"] = "expired", ["include_comments"] = true });

        Assert.Equal("?label=factory&claimState=expired", Assert.Single(server.Api.Requests).Query);
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }

    [Theory]
    [InlineData("cursor", "garbage")]
    [InlineData("updated_since", "yesterday")]
    [InlineData("claim_state", "mine")]
    public async Task An_argument_the_api_refuses_becomes_the_typed_invalid_argument_error(string argument, string value)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.BadRequest, $$"""{"code":"invalid_argument","message":"Argument '{{argument}}' is malformed."}"""));

        var result = await server.CallAsync("list_work_items", new() { [argument] = value });

        var message = AssertError(result, KanbanErrorCodes.InvalidArgument);
        Assert.Contains(argument, message, StringComparison.Ordinal);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
