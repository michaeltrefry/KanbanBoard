using System.Net;
using System.Text.Json;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// The project tools as an orchestrator sees them: the real MCP server over HTTP, a faked API.
public sealed class KanbanToolsProjectTests
{
    // Without a description: the MCP server leaves a null field out, unless the field says otherwise.
    private const string TargetJson =
        """{"id":"22222222-2222-2222-2222-222222222222","name":"App","key":"APP","isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","totalItems":3,"openItems":2,"repository_url":"https://github.com/acme/app.git","default_branch":"main"}""";

    private const string PlainJson =
        """{"id":"55555555-5555-5555-5555-555555555555","name":"Plain","key":"PLAIN","isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","totalItems":0,"openItems":0,"repository_url":null,"default_branch":null}""";

    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Project_tools_are_listed_in_snake_case_with_their_arguments()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, TargetJson));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        // includeArchived is the argument list_projects has always had; name, key and description those of create_project.
        Assert.Equal(["includeArchived"], ArgumentNames(tools["list_projects"]));
        Assert.False(tools["list_projects"].JsonSchema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0);
        Assert.Equal(["default_branch", "description", "idempotency_key", "key", "name", "repository_url"], ArgumentNames(tools["create_project"]));
        Assert.Equal(["key", "name"], RequiredArguments(tools["create_project"]));
        Assert.Equal(["clear", "default_branch", "description", "id", "is_archived", "key", "name", "repository_url"], ArgumentNames(tools["update_project"]));
        Assert.Equal(["id"], RequiredArguments(tools["update_project"]));
    }

    [Fact]
    public async Task List_returns_repository_url_and_default_branch_for_every_project_null_when_unset()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, $"[{TargetJson},{PlainJson}]"));

        // The calls as they were made before the fields existed.
        var listed = await server.CallAsync("list_projects", new());
        var withArchived = await server.CallAsync("list_projects", new() { ["includeArchived"] = true });

        Assert.NotEqual(true, listed.IsError);
        Assert.NotEqual(true, withArchived.IsError);
        Assert.Equal(["?includeArchived=False", "?includeArchived=True"], server.Api.Requests.Select(request => request.Query).ToArray());
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Get, "/api/projects"), (request.Method, request.Path)));
        // The projects exactly as the API sent them: a project without a target carries both fields as null.
        Assert.Equal($"[{TargetJson},{PlainJson}]", Text(listed));
    }

    [Fact]
    public async Task Create_posts_the_repository_url_default_branch_and_idempotency_key_and_says_whether_it_created()
    {
        var createdJson = TargetJson[..^1] + ""","created":true}""";
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, createdJson));

        var result = await server.CallAsync("create_project", new()
        {
            ["name"] = "App", ["key"] = "APP", ["repository_url"] = "https://github.com/acme/app.git", ["default_branch"] = "main", ["idempotency_key"] = "plan-1"
        });

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Post, "/api/projects"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(
            ("App", "APP", "https://github.com/acme/app.git", "main", "plan-1", false),
            (body.RootElement.GetProperty("name").GetString(), body.RootElement.GetProperty("key").GetString(),
                body.RootElement.GetProperty("repositoryUrl").GetString(), body.RootElement.GetProperty("defaultBranch").GetString(),
                body.RootElement.GetProperty("idempotencyKey").GetString(), body.RootElement.GetProperty("isArchived").GetBoolean()));
        Assert.Equal(createdJson, Text(result));
    }

    [Fact]
    public async Task Create_is_still_callable_with_the_arguments_it_always_had()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, PlainJson));

        var result = await server.CallAsync("create_project", new() { ["name"] = "Plain", ["key"] = "PLAIN", ["description"] = "d" });

        Assert.NotEqual(true, result.IsError);
        using var body = JsonDocument.Parse(Assert.Single(server.Api.Requests).Body);
        Assert.Equal(("Plain", "PLAIN", "d"), (body.RootElement.GetProperty("name").GetString(), body.RootElement.GetProperty("key").GetString(), body.RootElement.GetProperty("description").GetString()));
        Assert.Equal(
            (JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null),
            (body.RootElement.GetProperty("repositoryUrl").ValueKind, body.RootElement.GetProperty("defaultBranch").ValueKind, body.RootElement.GetProperty("idempotencyKey").ValueKind));
        Assert.Contains("\"repository_url\":null", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_patches_only_the_arguments_passed()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, TargetJson));

        var branch = await server.CallAsync("update_project", new() { ["id"] = ProjectId, ["default_branch"] = "develop" });
        var several = await server.CallAsync("update_project", new()
        {
            ["id"] = ProjectId, ["name"] = "Renamed", ["key"] = "APP2", ["description"] = "d", ["repository_url"] = "git@github.com:acme/app.git", ["is_archived"] = true
        });
        var nothing = await server.CallAsync("update_project", new() { ["id"] = ProjectId });

        Assert.NotEqual(true, branch.IsError);
        Assert.NotEqual(true, several.IsError);
        Assert.NotEqual(true, nothing.IsError);
        Assert.All(server.Api.Requests, request => Assert.Equal((HttpMethod.Patch, $"/api/projects/{ProjectId}"), (request.Method, request.Path)));
        Assert.Equal(
            [
                """{"defaultBranch":"develop"}""",
                """{"name":"Renamed","key":"APP2","description":"d","repositoryUrl":"git@github.com:acme/app.git","isArchived":true}""",
                "{}"
            ],
            server.Api.Requests.Select(request => request.Body).ToArray());
        Assert.Equal(TargetJson, Text(branch));
    }

    [Fact]
    public async Task Update_clears_the_fields_named_in_clear_by_sending_an_explicit_null()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, PlainJson));

        var result = await server.CallAsync("update_project", new()
        {
            ["id"] = ProjectId, ["name"] = "Renamed", ["clear"] = new[] { "default_branch", "repository_url", "description" }
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(
            """{"name":"Renamed","description":null,"repositoryUrl":null,"defaultBranch":null}""",
            Assert.Single(server.Api.Requests).Body);
        // A cleared field comes back as null, not left out.
        Assert.Contains("\"repository_url\":null,\"default_branch\":null", Text(result), StringComparison.Ordinal);
    }

    public static TheoryData<Dictionary<string, object?>> RefusedClears => new()
    {
        // Not a field of a project.
        new Dictionary<string, object?> { ["clear"] = new[] { "branch" } },
        // A field that cannot be empty.
        new Dictionary<string, object?> { ["clear"] = new[] { "name" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "key" } },
        new Dictionary<string, object?> { ["clear"] = new[] { "is_archived" } },
        new Dictionary<string, object?> { ["clear"] = new string?[] { null } },
        new Dictionary<string, object?> { ["clear"] = new string?[] { "default_branch", null } },
        // Names are matched exactly.
        new Dictionary<string, object?> { ["clear"] = new[] { "Default_Branch" } },
        // Set and cleared in one call.
        new Dictionary<string, object?> { ["default_branch"] = "main", ["clear"] = new[] { "default_branch" } },
        // An empty value is not a way to clear.
        new Dictionary<string, object?> { ["default_branch"] = "" },
        new Dictionary<string, object?> { ["repository_url"] = " " },
        new Dictionary<string, object?> { ["description"] = "" },
        new Dictionary<string, object?> { ["name"] = "" }
    };

    [Theory]
    [MemberData(nameof(RefusedClears))]
    public async Task Update_refuses_a_clear_it_cannot_carry_out_with_invalid_argument_before_any_api_call(Dictionary<string, object?> arguments)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, TargetJson));

        var result = await server.CallAsync("update_project", new(arguments) { ["id"] = ProjectId });

        AssertError(result, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(server.Api.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"code":"not_found","message":"no such project"}""", KanbanErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.Conflict, """{"code":"conflict","message":"Project key 'APP' already exists."}""", KanbanErrorCodes.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"Argument 'repository_url' ..."}""", KanbanErrorCodes.InvalidArgument)]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"scope_denied","message":"revoked"}""", KanbanErrorCodes.ScopeDenied)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"busy","message":"try again"}""", KanbanErrorCodes.Busy)]
    public async Task Api_refusal_of_a_project_write_becomes_the_typed_error_envelope(HttpStatusCode status, string apiBody, string code)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(status, apiBody));

        var created = await server.CallAsync("create_project", new() { ["name"] = "App", ["key"] = "APP", ["idempotency_key"] = "plan-1" });
        var updated = await server.CallAsync("update_project", new() { ["id"] = ProjectId, ["name"] = "Renamed" });

        AssertError(created, code);
        AssertError(updated, code);
    }

    [Fact]
    public async Task Read_scope_token_is_refused_scope_denied_by_the_project_writes_before_any_api_call_and_can_list()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, $"[{TargetJson}]"));

        var created = await server.CallAsync("create_project", new() { ["name"] = "App", ["key"] = "APP", ["idempotency_key"] = "plan-1" });
        var updated = await server.CallAsync("update_project", new() { ["id"] = ProjectId, ["is_archived"] = true });
        // Refused for its scope even when the arguments are malformed as well.
        var malformed = await server.CallAsync("update_project", new() { ["id"] = ProjectId, ["clear"] = new[] { "name" } });

        AssertError(created, KanbanErrorCodes.ScopeDenied);
        AssertError(updated, KanbanErrorCodes.ScopeDenied);
        AssertError(malformed, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(server.Api.Requests);
        Assert.NotEqual(true, (await server.CallAsync("list_projects", new())).IsError);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
