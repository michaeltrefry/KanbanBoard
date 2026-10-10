using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Mcp.Services;
using KanbanBoard.Mcp.Tools;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KanbanBoard.Tests;

public sealed class KanbanToolsScopeTests
{
    // Every MCP tool that creates, updates, moves, comments on, or deletes data.
    // Any tool not listed here is treated as a read tool and must work with a 'read' token using GETs only.
    private static readonly string[] WriteTools =
    [
        nameof(KanbanTools.CreateProject),
        nameof(KanbanTools.DeleteProject),
        nameof(KanbanTools.CreateEpic),
        nameof(KanbanTools.UpdateEpic),
        nameof(KanbanTools.DeleteEpic),
        nameof(KanbanTools.CreateEpicDocument),
        nameof(KanbanTools.UpdateEpicDocument),
        nameof(KanbanTools.AddWorkItemComment),
        nameof(KanbanTools.AddIssueComment),
        nameof(KanbanTools.CreateWorkItem),
        nameof(KanbanTools.CreateEpicWorkItem),
        nameof(KanbanTools.MoveWorkItem),
        nameof(KanbanTools.UpdateWorkItem),
        nameof(KanbanTools.DeleteWorkItem)
    ];

    public static TheoryData<string> WriteToolNames => new(WriteTools);

    public static TheoryData<string> ReadToolNames => new(ToolMethods()
        .Select(method => method.Name)
        .Where(name => !WriteTools.Contains(name)));

    [Fact]
    public void Write_tool_list_matches_existing_tools()
    {
        var toolNames = ToolMethods().Select(method => method.Name).ToHashSet();
        Assert.All(WriteTools, name => Assert.Contains(name, toolNames));
        var readTools = toolNames.Except(WriteTools).ToHashSet();
        Assert.Contains(nameof(KanbanTools.ListProjects), readTools);
        Assert.Contains(nameof(KanbanTools.GetBoard), readTools);
        Assert.Contains(nameof(KanbanTools.ListWorkItems), readTools);
        Assert.Contains(nameof(KanbanTools.GetEpic), readTools);
    }

    [Theory]
    [MemberData(nameof(WriteToolNames))]
    public async Task Write_tool_with_read_token_returns_scope_error_without_calling_api(string toolName)
    {
        var api = new FakeApi();
        var tools = CreateTools(api, PersonalAccessTokenScopes.Read);

        var exception = await Assert.ThrowsAsync<McpException>(() => InvokeAsync(tools, toolName));

        Assert.Equal(KanbanTools.ReadOnlyScopeErrorMessage, exception.Message);
        Assert.Empty(api.Requests);
    }

    [Theory]
    [MemberData(nameof(ReadToolNames))]
    public async Task Read_tool_with_read_token_calls_api_with_gets_only(string toolName)
    {
        var api = new FakeApi();
        var tools = CreateTools(api, PersonalAccessTokenScopes.Read);

        await InvokeAsync(tools, toolName);

        Assert.NotEmpty(api.Requests);
        Assert.All(api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData(PersonalAccessTokenScopes.Full)]
    [InlineData(null)]
    public async Task Write_tools_with_full_token_or_without_pat_reach_api(string? scope)
    {
        var api = new FakeApi();
        // Without a token, writes are only allowed when PAT authentication is not required.
        var tools = CreateTools(api, scope, requirePersonalAccessToken: scope is not null);

        await tools.CreateWorkItem(Guid.NewGuid(), "Title", WorkItemType.Story);
        await tools.MoveWorkItem(Guid.NewGuid(), WorkItemStatus.InProgress);
        await tools.DeleteWorkItem(Guid.NewGuid());
        await tools.AddWorkItemComment(Guid.NewGuid(), "claude", "hello");

        Assert.Equal(
            [HttpMethod.Post, HttpMethod.Post, HttpMethod.Delete, HttpMethod.Post],
            api.Requests.Select(request => request.Method).ToArray());
    }

    [Theory]
    [MemberData(nameof(WriteToolNames))]
    public async Task Write_tool_without_token_when_pat_required_fails_closed_without_calling_api(string toolName)
    {
        var api = new FakeApi();
        var tools = CreateTools(api, scope: null, requirePersonalAccessToken: true);

        var exception = await Assert.ThrowsAsync<McpException>(() => InvokeAsync(tools, toolName));

        Assert.Equal(KanbanTools.MissingTokenErrorMessage, exception.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Validator_caches_scope_and_defaults_missing_scope_to_full()
    {
        var calls = 0;
        var tokenId = Guid.NewGuid();
        string? scopeInResponse = PersonalAccessTokenScopes.Read;
        using var services = CreateValidatorServices(_ =>
        {
            calls++;
            var json = scopeInResponse is null
                ? $$"""{"appUserId":"{{Guid.NewGuid()}}","personalAccessTokenId":"{{tokenId}}","displayName":null,"email":null,"expiresAtUtc":null}"""
                : $$"""{"appUserId":"{{Guid.NewGuid()}}","personalAccessTokenId":"{{tokenId}}","displayName":null,"email":null,"expiresAtUtc":null,"scope":"{{scopeInResponse}}"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var validator = services.GetRequiredService<IMcpPersonalAccessTokenValidator>();

        var first = await validator.ValidateAsync("kbp_read_token", CancellationToken.None);
        var cached = await validator.ValidateAsync("kbp_read_token", CancellationToken.None);
        scopeInResponse = null;
        var legacy = await validator.ValidateAsync("kbp_legacy_token", CancellationToken.None);

        Assert.Equal(PersonalAccessTokenScopes.Read, first!.Scope);
        Assert.False(first.AllowsWrites);
        Assert.Equal(PersonalAccessTokenScopes.Read, cached!.Scope);
        Assert.Equal(PersonalAccessTokenScopes.Full, legacy!.Scope);
        Assert.True(legacy.AllowsWrites);
        Assert.Equal(2, calls);
    }

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(KanbanTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static async Task InvokeAsync(KanbanTools tools, string toolName)
    {
        var method = ToolMethods().Single(candidate => candidate.Name == toolName);
        var arguments = method.GetParameters().Select(DummyArgument).ToArray();
        try
        {
            var task = (Task)method.Invoke(tools, arguments)!;
            await task;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static object? DummyArgument(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }

        var type = parameter.ParameterType;
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(string)) return "value";
        if (type == typeof(int)) return 0;
        if (type == typeof(bool)) return false;
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        return null;
    }

    private static KanbanTools CreateTools(FakeApi api, string? scope, bool requirePersonalAccessToken = true)
    {
        var httpContext = new DefaultHttpContext();
        if (scope is not null)
        {
            httpContext.Items[typeof(McpAuthenticatedToken)] =
                new McpAuthenticatedToken(Guid.NewGuid(), Guid.NewGuid(), null, null, null, scope);
        }

        var services = new ServiceCollection();
        services.AddSingleton(api);
        services
            .AddHttpClient("kanban-api", client => client.BaseAddress = new Uri("https://kanban.example.test"))
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<FakeApi>());
        var provider = services.BuildServiceProvider();

        return new KanbanTools(
            provider.GetRequiredService<IHttpClientFactory>(),
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(new McpAuthenticationOptions { RequirePersonalAccessToken = requirePersonalAccessToken }));
    }

    private static ServiceProvider CreateValidatorServices(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton(Options.Create(new McpAuthenticationOptions
        {
            RequirePersonalAccessToken = true,
            ValidationCacheSeconds = 30
        }));
        services.AddSingleton<IMcpPersonalAccessTokenValidator, McpPersonalAccessTokenValidator>();
        services
            .AddHttpClient("kanban-api", client => client.BaseAddress = new Uri("https://kanban.example.test"))
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler(respond));
        return services.BuildServiceProvider();
    }

    // Answers every call with a JSON null body, which the tools treat as "nothing found".
    private sealed class FakeApi : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/projects"
                ? "[]"
                : "null";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
