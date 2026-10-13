using System.Net;
using System.Text.Json;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// The comment tools as an orchestrator sees them: the real MCP server over HTTP, a faked API.
public sealed class KanbanToolsCommentTests
{
    private const string CommentJson =
        """{"id":"77777777-7777-7777-7777-777777777777","workItemId":"11111111-1111-1111-1111-111111111111","author":"Factory","body":"shipped","createdAtUtc":"2026-10-12T12:00:00+00:00","author_id":"33333333-3333-3333-3333-333333333333"}""";

    private const string CommentedItemJson =
        $$"""{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","epicId":null,"epic":null,"title":"Item","description":null,"type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":null,"comments":[{{CommentJson}}],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[],"dependents":[],"links":[],"comment_id":"77777777-7777-7777-7777-777777777777","comment_created":true}""";

    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Comment_tools_are_listed_in_snake_case_with_their_arguments()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, CommentedItemJson));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        // itemId, author and body are the arguments the tool has always had; author is no longer required.
        Assert.Equal(["author", "body", "idempotency_key", "itemId"], ArgumentNames(tools["add_work_item_comment"]));
        Assert.Equal(["body", "itemId"], RequiredArguments(tools["add_work_item_comment"]));
        // add_issue_comment keeps its three required arguments and gains the optional key.
        Assert.Equal(["author", "body", "idempotency_key", "itemId"], ArgumentNames(tools["add_issue_comment"]));
        Assert.Equal(["author", "body", "itemId"], RequiredArguments(tools["add_issue_comment"]));
        Assert.Equal(["cursor", "id", "limit"], ArgumentNames(tools["list_work_item_comments"]));
        Assert.Equal(["id"], RequiredArguments(tools["list_work_item_comments"]));
    }

    [Fact]
    public async Task Add_posts_the_body_author_and_idempotency_key_and_returns_the_item_with_the_comment_id()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, CommentedItemJson));

        var result = await server.CallAsync("add_work_item_comment", new()
        {
            ["itemId"] = ItemId, ["author"] = "someone else", ["body"] = "shipped", ["idempotency_key"] = "run-1:done"
        });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/items/{ItemId}/comments"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("someone else", body.RootElement.GetProperty("author").GetString());
        Assert.Equal("shipped", body.RootElement.GetProperty("body").GetString());
        Assert.Equal("run-1:done", body.RootElement.GetProperty("idempotencyKey").GetString());
        var text = Text(result);
        Assert.Contains("\"comment_id\":\"77777777-7777-7777-7777-777777777777\"", text, StringComparison.Ordinal);
        Assert.Contains("\"comment_created\":true", text, StringComparison.Ordinal);
        // The comment exactly as the API sent it: same names, same values.
        Assert.Contains($"\"comments\":[{CommentJson}]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_issue_comment_posts_its_idempotency_key_to_the_same_endpoint_and_returns_the_comment_of_a_repeat()
    {
        var repeated = CommentedItemJson.Replace("\"comment_created\":true", "\"comment_created\":false", StringComparison.Ordinal);
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, repeated));

        var viaIssue = await server.CallAsync("add_issue_comment", new() { ["itemId"] = ItemId, ["author"] = "claude", ["body"] = "shipped", ["idempotency_key"] = "run-1:done" });
        var viaItem = await server.CallAsync("add_work_item_comment", new() { ["itemId"] = ItemId, ["author"] = "claude", ["body"] = "shipped", ["idempotency_key"] = "run-1:done" });

        Assert.NotEqual(true, viaIssue.IsError);
        // The two tools send the same request, so they share their keys: the API scopes a key to the item and the caller.
        Assert.Equal(2, server.Api.Requests.Count);
        Assert.Equal(server.Api.Requests[1], server.Api.Requests[0]);
        Assert.Equal((HttpMethod.Post, $"/api/items/{ItemId}/comments"), (server.Api.Requests[0].Method, server.Api.Requests[0].Path));
        using var body = JsonDocument.Parse(server.Api.Requests[0].Body);
        Assert.Equal("run-1:done", body.RootElement.GetProperty("idempotencyKey").GetString());
        // The earlier comment, as the API reports it for a repeat.
        Assert.Equal(Text(viaItem), Text(viaIssue));
        Assert.Contains("\"comment_id\":\"77777777-7777-7777-7777-777777777777\"", Text(viaIssue), StringComparison.Ordinal);
        Assert.Contains("\"comment_created\":false", Text(viaIssue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_is_still_callable_with_the_arguments_it_always_had()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, CommentedItemJson));

        var added = await server.CallAsync("add_work_item_comment", new() { ["itemId"] = ItemId, ["author"] = "claude", ["body"] = "note" });
        var addedToIssue = await server.CallAsync("add_issue_comment", new() { ["itemId"] = ItemId, ["author"] = "claude", ["body"] = "note" });
        var withoutAuthor = await server.CallAsync("add_work_item_comment", new() { ["itemId"] = ItemId, ["body"] = "note" });

        Assert.NotEqual(true, added.IsError);
        Assert.NotEqual(true, addedToIssue.IsError);
        Assert.NotEqual(true, withoutAuthor.IsError);
        Assert.All(server.Api.Requests.Take(2), request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal(("claude", "note", JsonValueKind.Null),
                (body.RootElement.GetProperty("author").GetString(), body.RootElement.GetProperty("body").GetString(), body.RootElement.GetProperty("idempotencyKey").ValueKind));
        });
    }

    [Fact]
    public async Task An_item_read_does_not_carry_the_fields_of_a_comment_write()
    {
        var itemJson = CommentedItemJson.Replace(",\"comment_id\":\"77777777-7777-7777-7777-777777777777\",\"comment_created\":true", string.Empty, StringComparison.Ordinal);
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, itemJson));

        var result = await server.CallAsync("get_work_item", new() { ["itemId"] = ItemId });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains($"\"comments\":[{CommentJson}]", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("comment_id", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("comment_created", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_gets_one_page_with_limit_and_cursor_and_returns_the_comments_and_the_next_cursor()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.OK, $$"""{"items":[{{CommentJson}}],"next_cursor":"bmV4dA"}"""));

        var first = await server.CallAsync("list_work_item_comments", new() { ["id"] = ItemId });
        var next = await server.CallAsync("list_work_item_comments", new() { ["id"] = ItemId, ["limit"] = 25, ["cursor"] = "cHJl+dg" });

        Assert.NotEqual(true, first.IsError);
        Assert.NotEqual(true, next.IsError);
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Get, $"/api/items/{ItemId}/comments"), (request.Method, request.Path)));
        Assert.Equal([string.Empty, "?limit=25&cursor=cHJl%2Bdg"], server.Api.Requests.Select(request => request.Query).ToArray());
        Assert.Equal($$"""{"items":[{{CommentJson}}],"next_cursor":"bmV4dA"}""", Text(first));
    }

    [Fact]
    public async Task List_leaves_next_cursor_out_on_the_last_page()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ =>
            Json(HttpStatusCode.OK, """{"items":[],"next_cursor":null}"""));

        var result = await server.CallAsync("list_work_item_comments", new() { ["id"] = ItemId });

        Assert.Equal("""{"items":[]}""", Text(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"Argument 'idempotency_key' must be 1 to 128 printable ASCII characters without spaces."}""", KanbanErrorCodes.InvalidArgument)]
    [InlineData(HttpStatusCode.NotFound, """{"code":"not_found","message":"no such item"}""", KanbanErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"scope_denied","message":"revoked"}""", KanbanErrorCodes.ScopeDenied)]
    public async Task Api_refusal_of_a_comment_call_becomes_the_typed_error_envelope(HttpStatusCode status, string apiBody, string code)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(status, apiBody));

        var added = await server.CallAsync("add_work_item_comment", new() { ["itemId"] = ItemId, ["body"] = "note", ["idempotency_key"] = "two words" });
        var addedToIssue = await server.CallAsync("add_issue_comment", new() { ["itemId"] = ItemId, ["author"] = "claude", ["body"] = "note" });
        var listed = await server.CallAsync("list_work_item_comments", new() { ["id"] = ItemId, ["cursor"] = "x" });

        AssertError(added, code);
        AssertError(addedToIssue, code);
        AssertError(listed, code);
    }

    [Theory]
    [InlineData("add_work_item_comment")]
    [InlineData("add_issue_comment")]
    public async Task Read_scope_token_is_refused_scope_denied_before_any_api_call(string tool)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, CommentedItemJson));

        var result = await server.CallAsync(tool, new() { ["itemId"] = ItemId, ["author"] = "claude", ["body"] = "note" });

        AssertError(result, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(server.Api.Requests);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
