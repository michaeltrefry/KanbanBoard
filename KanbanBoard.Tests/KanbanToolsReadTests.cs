using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Mcp.Tools;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

public sealed class KanbanToolsReadTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task GetWorkItem_returns_item_with_description_status_and_comments()
    {
        var itemId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var item = new WorkItemDto(
            itemId,
            projectId,
            null,
            null,
            "Fix parser",
            "Parser drops trailing tokens",
            WorkItemType.Issue,
            WorkItemStatus.InProgress,
            WorkItemPriority.High,
            0,
            null,
            null,
            [new WorkItemCommentDto(Guid.NewGuid(), itemId, "claude", "Repro attached", now)],
            now,
            now);
        var handler = new StubHandler(_ => JsonResponse(item));
        var tools = CreateTools(handler);

        var result = await tools.GetWorkItem(itemId);

        Assert.Equal($"/api/items/{itemId}", handler.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.Equal("Fix parser", result.Title);
        Assert.Equal("Parser drops trailing tokens", result.Description);
        Assert.Equal(WorkItemStatus.InProgress, result.Status);
        Assert.Equal("Repro attached", Assert.Single(result.Comments).Body);
    }

    [Fact]
    public async Task GetWorkItem_with_unknown_id_throws_not_found_error()
    {
        var itemId = Guid.NewGuid();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var tools = CreateTools(handler);

        var exception = await Assert.ThrowsAsync<KanbanToolException>(() => tools.GetWorkItem(itemId));

        Assert.Equal(KanbanErrorCodes.NotFound, exception.Code);

        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(itemId.ToString(), exception.Message);
    }

    [Fact]
    public async Task SearchWorkItems_calls_search_endpoint_with_query_project_and_limit()
    {
        var projectId = Guid.NewGuid();
        var hit = new WorkItemSearchResultDto(Guid.NewGuid(), projectId, "foo bar", WorkItemType.Story, WorkItemStatus.Ready);
        var handler = new StubHandler(_ => JsonResponse(new[] { hit }));
        var tools = CreateTools(handler);

        var results = await tools.SearchWorkItems("foo & bar", projectId, 10);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/items/search", request.RequestUri!.AbsolutePath);
        Assert.Equal(
            $"?query=foo%20%26%20bar&projectId={projectId}&limit=10",
            request.RequestUri.Query);
        Assert.Equal(hit, Assert.Single(results));
    }

    [Fact]
    public async Task SearchWorkItems_omits_optional_parameters()
    {
        var handler = new StubHandler(_ => JsonResponse(Array.Empty<WorkItemSearchResultDto>()));
        var tools = CreateTools(handler);

        var results = await tools.SearchWorkItems("foo");

        Assert.Equal("?query=foo", handler.Requests.Single().RequestUri!.Query);
        Assert.Empty(results);
    }

    // The url of the item's page, as the API makes it, passes through every read tool that returns work items; when
    // the API has no public address configured it is still written, as null.
    [Theory]
    [InlineData("\"https://kanban.example.com/stories/11111111-1111-1111-1111-111111111111\"")]
    [InlineData("null")]
    public async Task Read_tools_pass_the_url_of_the_item_page_through(string url)
    {
        var item = $$"""{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","title":"Item","type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","blockers":[],"dependents":[],"links":[],"version":1,"label_list":[],"url":{{url}}}""";
        var hit = $$"""{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","title":"Item","type":"Story","status":"Ready","estimate":null,"version":1,"url":{{url}}}""";
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, request =>
            Json(HttpStatusCode.OK, request.Path switch
            {
                "/api/items/search" => $"[{hit}]",
                "/api/items/by-link" => $"[{item}]",
                "/api/items" when request.Query.Contains("limit", StringComparison.Ordinal) => $$"""{"items":[{{item}}]}""",
                "/api/items" => $"[{item}]",
                _ => item
            }));

        CallToolResult[] results =
        [
            await server.CallAsync("get_work_item", new() { ["itemId"] = Guid.Parse("11111111-1111-1111-1111-111111111111") }),
            await server.CallAsync("list_work_items", []),
            await server.CallAsync("list_work_items", new() { ["limit"] = 5 }),
            await server.CallAsync("search_work_items", new() { ["query"] = "item" }),
            await server.CallAsync("find_work_items_by_link", new() { ["url"] = "https://example.test/pr/1" })
        ];

        Assert.All(results, result =>
        {
            Assert.NotEqual(true, result.IsError);
            Assert.Contains($"\"url\":{url}", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchWorkItems_rejects_blank_query_without_calling_api(string query)
    {
        var handler = new StubHandler(_ => JsonResponse(Array.Empty<WorkItemSearchResultDto>()));
        var tools = CreateTools(handler);

        var exception = await Assert.ThrowsAsync<KanbanToolException>(() => tools.SearchWorkItems(query));
        Assert.Equal(KanbanErrorCodes.InvalidArgument, exception.Code);
        Assert.Empty(handler.Requests);
    }

    private static KanbanTools CreateTools(HttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            new HttpContextAccessor(),
            Options.Create(new McpAuthenticationOptions()));

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: JsonOptions) };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api.test") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
