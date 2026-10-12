using System.Net;
using System.Text.Json;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// The link tools and the links field as an orchestrator sees them: the real MCP server over HTTP, a faked API.
public sealed class KanbanToolsLinkTests
{
    private const string LinksJson =
        """[{"id":"66666666-6666-6666-6666-666666666666","kind":"pr","url":"https://github.com/acme/app/pull/42","created_at_utc":"2026-10-12T12:00:00+00:00"}]""";

    private const string LinkedItemJson =
        $$"""{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","epicId":null,"epic":null,"title":"Item","description":null,"type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":null,"comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[],"dependents":[],"links":{{LinksJson}}}""";

    private const string PrUrl = "https://github.com/acme/app/pull/42";
    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LinkId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public async Task Link_tools_are_listed_in_snake_case_with_their_arguments()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, LinkedItemJson));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        Assert.Equal(["id", "kind", "url"], ArgumentNames(tools["add_work_item_link"]));
        Assert.Equal(["id", "kind", "url"], RequiredArguments(tools["add_work_item_link"]));
        Assert.Equal(["id", "kind", "link_id", "url"], ArgumentNames(tools["remove_work_item_link"]));
        Assert.Equal(["id"], RequiredArguments(tools["remove_work_item_link"]));
        Assert.Equal(["url"], ArgumentNames(tools["find_work_items_by_link"]));
        Assert.Equal(["url"], RequiredArguments(tools["find_work_items_by_link"]));
    }

    [Fact]
    public async Task Add_posts_kind_and_url_and_returns_the_item_with_its_links()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, LinkedItemJson));

        var result = await server.CallAsync("add_work_item_link", new() { ["id"] = ItemId, ["kind"] = "pr", ["url"] = PrUrl });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/items/{ItemId}/links"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("pr", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal(PrUrl, body.RootElement.GetProperty("url").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        AssertLinks(result);
    }

    [Fact]
    public async Task Remove_addresses_the_link_by_link_id_or_by_kind_and_url()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, LinkedItemJson));
        const string urlWithQuery = "https://example.com/a b?x=1&y=2#frag";

        var byId = await server.CallAsync("remove_work_item_link", new() { ["id"] = ItemId, ["link_id"] = LinkId });
        var byUrl = await server.CallAsync("remove_work_item_link", new() { ["id"] = ItemId, ["kind"] = "pr", ["url"] = urlWithQuery });

        Assert.NotEqual(true, byId.IsError);
        Assert.NotEqual(true, byUrl.IsError);
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Delete, $"/api/items/{ItemId}/links"), (request.Method, request.Path)));
        Assert.Equal($"?linkId={LinkId}", server.Api.Requests[0].Query);
        Assert.Equal($"?kind=pr&url={Uri.EscapeDataString(urlWithQuery)}", server.Api.Requests[1].Query);
        AssertLinks(byId);
    }

    [Fact]
    public async Task Find_gets_the_items_by_the_escaped_url_and_returns_them_with_their_links()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, $"[{LinkedItemJson}]"));
        const string urlWithQuery = "https://example.com/pull/1?x=1&y=2#frag";

        var result = await server.CallAsync("find_work_items_by_link", new() { ["url"] = urlWithQuery });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Get, "/api/items/by-link", $"?url={Uri.EscapeDataString(urlWithQuery)}"), (request.Method, request.Path, request.Query));
        Assert.StartsWith($"[{{\"id\":\"{ItemId}\"", Text(result), StringComparison.Ordinal);
        AssertLinks(result);
    }

    [Fact]
    public async Task Find_returns_an_empty_array_for_an_unknown_url()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, "[]"));

        var result = await server.CallAsync("find_work_items_by_link", new() { ["url"] = PrUrl });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("[]", Text(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"Argument 'kind' must be one of: pr, branch, other."}""", KanbanErrorCodes.InvalidArgument)]
    [InlineData(HttpStatusCode.NotFound, """{"code":"not_found","message":"no such item"}""", KanbanErrorCodes.NotFound)]
    public async Task Api_refusal_of_a_link_call_becomes_the_typed_error_envelope(HttpStatusCode status, string apiBody, string code)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(status, apiBody));

        var added = await server.CallAsync("add_work_item_link", new() { ["id"] = ItemId, ["kind"] = "issue", ["url"] = PrUrl });
        var removed = await server.CallAsync("remove_work_item_link", new() { ["id"] = ItemId });
        var found = await server.CallAsync("find_work_items_by_link", new() { ["url"] = " " });

        AssertError(added, code);
        AssertError(removed, code);
        AssertError(found, code);
    }

    [Theory]
    [InlineData("add_work_item_link")]
    [InlineData("remove_work_item_link")]
    public async Task Read_scope_token_is_refused_scope_denied_before_any_api_call(string tool)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, LinkedItemJson));

        var result = await server.CallAsync(tool, new() { ["id"] = ItemId, ["kind"] = "pr", ["url"] = PrUrl });

        AssertError(result, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(server.Api.Requests);
    }

    [Theory]
    [InlineData("get_work_item", "itemId")]
    [InlineData("list_work_items", null)]
    [InlineData("list_epic_work_items", "epicId")]
    [InlineData("get_issues", null)]
    [InlineData("get_board", "projectId")]
    public async Task Read_tools_return_the_links_to_a_read_scope_token(string tool, string? idArgument)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, request =>
            Json(HttpStatusCode.OK, request.Path.StartsWith("/api/projects/", StringComparison.Ordinal)
                ? $$"""{"id":"22222222-2222-2222-2222-222222222222","name":"P","key":"P","description":null,"isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","items":[{{LinkedItemJson}}]}"""
                : request.Path == "/api/items" ? $"[{LinkedItemJson}]" : LinkedItemJson));

        var result = await server.CallAsync(tool, idArgument is null ? [] : new() { [idArgument] = ItemId });

        Assert.NotEqual(true, result.IsError);
        Assert.All(server.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        AssertLinks(result);
    }

    [Fact]
    public async Task A_page_of_list_work_items_returns_the_links()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.OK, $$"""{"items":[{{LinkedItemJson}}],"next_cursor":null}"""));

        var result = await server.CallAsync("list_work_items", new() { ["limit"] = 1 });

        Assert.NotEqual(true, result.IsError);
        AssertLinks(result);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    // The links exactly as the API sent them: same names, same values.
    private static void AssertLinks(CallToolResult result) =>
        Assert.Contains($"\"links\":{LinksJson}", Text(result), StringComparison.Ordinal);
}
