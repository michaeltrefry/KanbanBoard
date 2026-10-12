using System.Net;
using System.Text;
using System.Text.Json;
using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Mcp.Services;
using KanbanBoard.Mcp.Tools;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KanbanBoard.Tests;

// Drives the real MCP server over HTTP with an MCP client, so tool names, argument names and the error
// envelope are checked as an orchestrator sees them. Only the Kanban API behind it is faked.
public sealed class KanbanToolsClaimTests
{
    private const string ClaimedItemJson =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","epicId":null,"epic":null,"title":"Item","description":null,"type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":null,"comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":"Factory","claimant_id":"33333333-3333-3333-3333-333333333333","claim_marker":"run-1","claim_expires_at_utc":"2026-10-12T12:01:00+00:00"}""";

    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Claim_tools_are_listed_with_snake_case_names_and_arguments_and_read_tools_keep_their_names()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ClaimedItemJson));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        Assert.Equal(["id", "marker", "ttl_seconds"], ArgumentNames(tools["claim_work_item"]));
        Assert.Equal(["id", "marker", "ttl_seconds"], ArgumentNames(tools["renew_work_item_claim"]));
        Assert.Equal(["id", "marker"], ArgumentNames(tools["release_work_item_claim"]));
        Assert.Equal(["id", "marker", "ttl_seconds"], RequiredArguments(tools["claim_work_item"]));
        Assert.All(
            new[]
            {
                "list_projects", "get_board", "list_epics", "get_epic", "list_epic_documents", "get_epic_document",
                "list_work_items", "list_epic_work_items", "get_issues", "get_work_item", "search_work_items"
            },
            name => Assert.Contains(name, tools.Keys));
    }

    [Fact]
    public async Task Claim_posts_marker_and_ttl_and_returns_the_item_with_claimant_marker_and_expiry()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ClaimedItemJson));

        var result = await server.CallAsync("claim_work_item", new() { ["id"] = ItemId, ["marker"] = "run-1", ["ttl_seconds"] = 60 });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/items/{ItemId}/claim"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("run-1", body.RootElement.GetProperty("marker").GetString());
        Assert.Equal(60, body.RootElement.GetProperty("ttlSeconds").GetInt32());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        AssertClaimFields(result);
    }

    [Theory]
    [InlineData("renew_work_item_claim", "/claim/renew")]
    [InlineData("release_work_item_claim", "/claim/release")]
    public async Task Renew_and_release_post_to_their_own_routes(string tool, string route)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ClaimedItemJson));
        var arguments = new Dictionary<string, object?> { ["id"] = ItemId, ["marker"] = "run-1" };
        if (tool == "renew_work_item_claim")
        {
            arguments["ttl_seconds"] = 120;
        }

        var result = await server.CallAsync(tool, arguments);

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/items/{ItemId}{route}"), (request.Method, request.Path));
        Assert.Contains("\"marker\":\"run-1\"", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("get_work_item", "itemId")]
    [InlineData("list_work_items", null)]
    [InlineData("list_epic_work_items", "epicId")]
    [InlineData("get_issues", null)]
    [InlineData("get_board", "projectId")]
    public async Task Read_tools_return_claimant_marker_and_expiry(string tool, string? idArgument)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, request =>
            Json(HttpStatusCode.OK, request.Path.StartsWith("/api/projects/", StringComparison.Ordinal)
                ? $$"""{"id":"22222222-2222-2222-2222-222222222222","name":"P","key":"P","description":null,"isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","items":[{{ClaimedItemJson}}]}"""
                : request.Path == "/api/items" ? $"[{ClaimedItemJson}]" : ClaimedItemJson));

        var result = await server.CallAsync(tool, idArgument is null ? [] : new() { [idArgument] = ItemId });

        Assert.NotEqual(true, result.IsError);
        Assert.All(server.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        AssertClaimFields(result);
    }

    [Theory]
    [InlineData("claim_work_item")]
    [InlineData("renew_work_item_claim")]
    [InlineData("release_work_item_claim")]
    public async Task Read_scope_token_is_refused_scope_denied_before_any_api_call(string tool)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, ClaimedItemJson));
        var arguments = new Dictionary<string, object?> { ["id"] = ItemId, ["marker"] = "run-1" };
        if (tool != "release_work_item_claim")
        {
            arguments["ttl_seconds"] = 60;
        }

        var result = await server.CallAsync(tool, arguments);

        AssertError(result, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(server.Api.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"code":"already_claimed","message":"held by someone else"}""", KanbanErrorCodes.AlreadyClaimed, "held by someone else")]
    [InlineData(HttpStatusCode.Conflict, """{"code":"conflict","message":"claim lapsed"}""", KanbanErrorCodes.Conflict, "claim lapsed")]
    [InlineData(HttpStatusCode.NotFound, """{"code":"not_found","message":"no such item"}""", KanbanErrorCodes.NotFound, "no such item")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"bad ttl"}""", KanbanErrorCodes.InvalidArgument, "bad ttl")]
    // Refusals from API paths that predate error codes carry only a message, or nothing.
    [InlineData(HttpStatusCode.Forbidden, """{"message":"read scope"}""", KanbanErrorCodes.ScopeDenied, "read scope")]
    [InlineData(HttpStatusCode.NotFound, "", KanbanErrorCodes.NotFound, null)]
    [InlineData(HttpStatusCode.Conflict, "<html>proxy</html>", KanbanErrorCodes.Conflict, null)]
    public async Task Api_refusal_becomes_the_typed_error_envelope(HttpStatusCode status, string apiBody, string code, string? message)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(status, apiBody));

        var result = await server.CallAsync("claim_work_item", new() { ["id"] = ItemId, ["marker"] = "run-2", ["ttl_seconds"] = 60 });

        var actualMessage = AssertError(result, code);
        if (message is not null)
        {
            Assert.Equal(message, actualMessage);
        }
    }

    [Fact]
    public async Task Unknown_work_item_read_is_refused_not_found()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.NotFound, ""));

        var result = await server.CallAsync("get_work_item", new() { ["itemId"] = ItemId });

        Assert.Contains(ItemId.ToString(), AssertError(result, KanbanErrorCodes.NotFound), StringComparison.Ordinal);
    }

    internal static string[] ArgumentNames(McpClientTool tool) =>
        tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToArray();

    internal static string[] RequiredArguments(McpClientTool tool) =>
        tool.JsonSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!).Order().ToArray();

    private static void AssertClaimFields(CallToolResult result)
    {
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("\"claimant\":\"Factory\"", text, StringComparison.Ordinal);
        Assert.Contains("\"claimant_id\":\"33333333-3333-3333-3333-333333333333\"", text, StringComparison.Ordinal);
        Assert.Contains("\"claim_marker\":\"run-1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"claim_expires_at_utc\":\"2026-10-12T12:01:00", text, StringComparison.Ordinal);
    }

    // The envelope: isError, structuredContent {code, message}, and one text block holding the same JSON.
    internal static string AssertError(CallToolResult result, string code)
    {
        Assert.True(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Equal(code, structured.GetProperty("code").GetString());
        var message = structured.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));

        using var text = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(code, text.RootElement.GetProperty("code").GetString());
        Assert.Equal(message, text.RootElement.GetProperty("message").GetString());
        return message!;
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed record RecordedRequest(HttpMethod Method, string Path, string Query, string Body);

    internal sealed class FakeApi(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(recorded);
            return respond(recorded);
        }
    }

    internal sealed class McpFixture(IHost host, McpClient client, FakeApi api) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;

        public FakeApi Api { get; } = api;

        public static async Task<McpFixture> CreateAsync(string scope, Func<RecordedRequest, HttpResponseMessage> respond)
        {
            var api = new FakeApi(respond);
            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddHttpContextAccessor();
                        services.AddSingleton(Options.Create(new McpAuthenticationOptions { RequirePersonalAccessToken = true }));
                        services
                            .AddHttpClient("kanban-api", client => client.BaseAddress = new Uri("http://api.test"))
                            .ConfigurePrimaryHttpMessageHandler(() => api);
                        // The same registration as KanbanBoard.Mcp/Program.cs.
                        services.AddMcpServer().WithHttpTransport().WithTools<KanbanTools>().WithKanbanToolErrors();
                    });
                    webBuilder.Configure(app =>
                    {
                        // Stands in for the bearer-token middleware: the request is already authenticated with this scope.
                        app.Use(async (context, next) =>
                        {
                            context.Items[typeof(McpAuthenticatedToken)] =
                                new McpAuthenticatedToken(Guid.NewGuid(), Guid.NewGuid(), "Factory", null, null, scope);
                            await next(context);
                        });
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
                    });
                })
                .StartAsync();

            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
                host.GetTestClient(),
                ownsHttpClient: true);
            return new McpFixture(host, await McpClient.CreateAsync(transport), api);
        }

        public async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments) =>
            await Client.CallToolAsync(tool, arguments);

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            host.Dispose();
        }
    }
}
