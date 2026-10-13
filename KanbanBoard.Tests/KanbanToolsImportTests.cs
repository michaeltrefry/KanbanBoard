using System.Net;
using System.Text.Json;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// import_epic_plan and the idempotency keys of the create tools as an orchestrator sees them: the real MCP server
// over HTTP, a faked API.
public sealed class KanbanToolsImportTests
{
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EpicId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string ResultJson =
        """{"created":true,"project_id":"22222222-2222-2222-2222-222222222222","epic_id":"44444444-4444-4444-4444-444444444444","document_ids":["66666666-6666-6666-6666-666666666666"],"item_ids":{"A":"11111111-1111-1111-1111-111111111111","B":"55555555-5555-5555-5555-555555555555"}}""";

    private const string EpicJson =
        """{"id":"44444444-4444-4444-4444-444444444444","projectId":"22222222-2222-2222-2222-222222222222","name":"Epic","isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","created":false}""";

    private static Dictionary<string, object?> Plan() => new()
    {
        ["project_id"] = ProjectId,
        ["idempotency_key"] = "plan-1",
        ["epic"] = new { name = "Checkout", description = "d" },
        ["documents"] = new[] { new { title = "Plan", body = "P" } },
        ["items"] = new object[]
        {
            new { @ref = "A", title = "First", status = "Ready" },
            new { @ref = "B", title = "Second", type = "Task", priority = "High", estimate = 3, label_list = new[] { "api" }, blocked_by = new[] { "A" } }
        }
    };

    [Fact]
    public async Task The_import_tool_and_the_keys_of_the_create_tools_are_listed_in_snake_case()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ResultJson));

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        var import = tools["import_epic_plan"];
        Assert.Equal(["documents", "epic", "idempotency_key", "items", "project_id"], ArgumentNames(import));
        Assert.Equal(["epic", "idempotency_key", "items", "project_id"], RequiredArguments(import));
        var properties = import.JsonSchema.GetProperty("properties");
        string[] Fields(JsonElement schema) => schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(["name", "description"], Fields(properties.GetProperty("epic")));
        Assert.Equal(["title", "body"], Fields(properties.GetProperty("documents").GetProperty("items")));
        Assert.Equal(
            ["ref", "title", "type", "status", "priority", "description", "estimate", "labels", "label_list", "blocked_by"],
            Fields(properties.GetProperty("items").GetProperty("items")));
        Assert.Equal(["ref", "title"], properties.GetProperty("items").GetProperty("items").GetProperty("required").EnumerateArray().Select(name => name.GetString()).ToArray());

        // The create tools keep the arguments they had and gain the optional key.
        Assert.Equal(["description", "idempotency_key", "isArchived", "name", "projectId"], ArgumentNames(tools["create_epic"]));
        Assert.Equal(["name", "projectId"], RequiredArguments(tools["create_epic"]));
        Assert.Equal(["body", "epicId", "idempotency_key", "title"], ArgumentNames(tools["create_epic_document"]));
        Assert.Equal(["body", "epicId", "title"], RequiredArguments(tools["create_epic_document"]));
        Assert.Contains("idempotency_key", ArgumentNames(tools["create_work_item"]));
        Assert.Contains("idempotency_key", ArgumentNames(tools["create_epic_work_item"]));
        Assert.Equal(["projectId", "title", "type"], RequiredArguments(tools["create_work_item"]));
        Assert.Equal(["epicId", "title"], RequiredArguments(tools["create_epic_work_item"]));
    }

    [Fact]
    public async Task Import_posts_the_plan_to_the_project_and_returns_the_ids()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, ResultJson));

        var result = await server.CallAsync("import_epic_plan", Plan());

        Assert.NotEqual(true, result.IsError);
        var request = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/projects/{ProjectId}/epic-plans"), (request.Method, request.Path));
        Assert.Equal(
            """{"epic":{"name":"Checkout","description":"d"},"items":[""" +
            """{"ref":"A","title":"First","type":null,"status":"Ready","priority":null,"description":null,"estimate":null,"labels":null,"label_list":null,"blocked_by":null},""" +
            """{"ref":"B","title":"Second","type":"Task","status":null,"priority":"High","description":null,"estimate":3,"labels":null,"label_list":["api"],"blocked_by":["A"]}],""" +
            "\"idempotency_key\":\"plan-1\",\"documents\":[{\"title\":\"Plan\",\"body\":\"P\"}]}",
            request.Body);
        Assert.Equal(ResultJson, Text(result));
    }

    [Fact]
    public async Task A_repeat_is_reported_as_the_api_reports_it()
    {
        var repeated = ResultJson.Replace("\"created\":true", "\"created\":false", StringComparison.Ordinal);
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, repeated));
        var plan = Plan();
        plan.Remove("documents");

        var result = await server.CallAsync("import_epic_plan", plan);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(repeated, Text(result));
        Assert.Contains("\"documents\":null", Assert.Single(server.Api.Requests).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, KanbanErrorCodes.Cycle)]
    [InlineData(HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument)]
    [InlineData(HttpStatusCode.NotFound, KanbanErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied)]
    [InlineData(HttpStatusCode.Conflict, KanbanErrorCodes.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable, KanbanErrorCodes.Busy)]
    public async Task A_refusal_of_the_api_is_returned_with_its_code_and_message(HttpStatusCode status, string code)
    {
        await using var server = await McpFixture.CreateAsync(
            PersonalAccessTokenScopes.Full, _ => Json(status, $$"""{"code":"{{code}}","message":"why"}"""));

        var result = await server.CallAsync("import_epic_plan", Plan());

        Assert.Equal("why", AssertError(result, code));
    }

    [Fact]
    public async Task A_field_the_plan_does_not_have_reaches_the_api_which_refuses_it()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"no field 'blockedBy'"}"""));
        var plan = Plan();
        plan["items"] = new object[] { new { @ref = "A", title = "First", blockedBy = new[] { "A" } } };

        var result = await server.CallAsync("import_epic_plan", plan);

        AssertError(result, KanbanErrorCodes.InvalidArgument);
        // Not dropped on the way: a misspelt blocked_by would otherwise import a plan without its dependencies.
        Assert.Contains("\"blockedBy\":[\"A\"]", Assert.Single(server.Api.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_larger_than_the_limit_is_refused_without_calling_the_api()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, ResultJson));
        var plan = Plan();
        plan["items"] = new object[] { new { @ref = "A", title = "First", description = new string('d', EpicPlanLimits.MaxRequestBytes) } };

        var result = await server.CallAsync("import_epic_plan", plan);

        Assert.Contains($"{EpicPlanLimits.MaxRequestBytes} bytes", AssertError(result, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
        Assert.Empty(server.Api.Requests);
    }

    public static TheoryData<string, object?> MalformedArguments => new()
    {
        { "items", "not a list" },
        { "items", new object[] { new { @ref = "A", title = "First", estimate = "five" } } },
        { "items", new object[] { new { @ref = "A", title = "First", blocked_by = "A" } } },
        { "items", null },
        { "epic", "Checkout" },
        { "documents", new { title = "Plan", body = "P" } },
        { "project_id", "not-a-guid" }
    };

    [Theory]
    [MemberData(nameof(MalformedArguments))]
    public async Task A_plan_that_is_not_of_the_shape_the_tool_takes_is_refused_invalid_argument_without_calling_the_api(string argument, object? value)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, ResultJson));
        var plan = Plan();
        if (value is null)
        {
            plan.Remove(argument);
        }
        else
        {
            plan[argument] = value;
        }

        var result = await server.CallAsync("import_epic_plan", plan);

        Assert.Contains("import_epic_plan", AssertError(result, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
        Assert.Empty(server.Api.Requests);
    }

    [Fact]
    public async Task A_response_of_the_api_that_cannot_be_read_stays_a_fault_without_a_code()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, "{not json"));

        var result = await server.CallAsync("import_epic_plan", Plan());

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public async Task The_create_tools_send_the_idempotency_key_and_return_created()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, request =>
            request.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, EpicJson) : Json(HttpStatusCode.OK, EpicJson));

        var epic = await server.CallAsync("create_epic", new() { ["projectId"] = ProjectId, ["name"] = "Epic", ["idempotency_key"] = "k-epic" });
        var document = await server.CallAsync("create_epic_document", new() { ["epicId"] = EpicId, ["title"] = "Doc", ["body"] = "b", ["idempotency_key"] = "k-doc" });
        var item = await server.CallAsync("create_work_item", new() { ["projectId"] = ProjectId, ["title"] = "Item", ["type"] = "Task", ["idempotency_key"] = "k-item" });
        var epicItem = await server.CallAsync("create_epic_work_item", new() { ["epicId"] = EpicId, ["title"] = "Item", ["idempotency_key"] = "k-epic-item" });

        Assert.All(new[] { epic, document, item, epicItem }, result => Assert.NotEqual(true, result.IsError));
        var posts = server.Api.Requests.Where(request => request.Method == HttpMethod.Post).ToList();
        Assert.Equal(
            [$"/api/projects/{ProjectId}/epics", $"/api/epics/{EpicId}/documents", "/api/items", "/api/items"],
            posts.Select(request => request.Path).ToArray());
        Assert.Equal(
            ["k-epic", "k-doc", "k-item", "k-epic-item"],
            posts.Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("idempotencyKey").GetString()).ToArray());
        // create_epic_work_item creates in the epic's project.
        Assert.Equal(ProjectId, JsonDocument.Parse(posts[3].Body).RootElement.GetProperty("projectId").GetGuid());
        Assert.Contains("\"created\":false", Text(epic), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_create_tools_are_still_callable_with_the_arguments_they_always_had()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.Created, EpicJson));

        var epic = await server.CallAsync("create_epic", new() { ["projectId"] = ProjectId, ["name"] = "Epic", ["description"] = "d", ["isArchived"] = false });
        var document = await server.CallAsync("create_epic_document", new() { ["epicId"] = EpicId, ["title"] = "Doc", ["body"] = "b" });
        var item = await server.CallAsync("create_work_item", new()
        {
            ["projectId"] = ProjectId, ["title"] = "Item", ["type"] = "Task", ["status"] = "Ready", ["priority"] = "High", ["epicId"] = EpicId,
            ["description"] = "d", ["estimate"] = 3, ["labels"] = "a,b"
        });

        Assert.All(new[] { epic, document, item }, result => Assert.NotEqual(true, result.IsError));
        Assert.All(server.Api.Requests, request =>
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(request.Body).RootElement.GetProperty("idempotencyKey").ValueKind));
    }

    [Theory]
    [InlineData("create_epic")]
    [InlineData("create_epic_document")]
    public async Task A_refusal_of_a_create_is_returned_with_its_code(string tool)
    {
        await using var server = await McpFixture.CreateAsync(
            PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"Argument 'idempotency_key' must be shorter."}"""));

        var result = await server.CallAsync(tool, tool == "create_epic"
            ? new() { ["projectId"] = ProjectId, ["name"] = "Epic", ["idempotency_key"] = "two words" }
            : new() { ["epicId"] = EpicId, ["title"] = "Doc", ["body"] = "b", ["idempotency_key"] = "two words" });

        Assert.Contains("idempotency_key", AssertError(result, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
