using System.Net;
using System.Text.Json;
using KanbanBoard.Mcp.Tools;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Protocol;
using static KanbanBoard.Tests.KanbanToolsClaimTests;

namespace KanbanBoard.Tests;

// What every tool does with the arguments of a call and with a refusal of the API, as an orchestrator sees it: the
// real MCP server over HTTP, a faked API.
public sealed class KanbanToolsArgumentTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private const string ItemJson =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","epicId":null,"epic":null,"title":"Item","description":null,"type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":"factory","comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[],"dependents":[],"links":[],"version":3,"label_list":["factory"],"url":null}""";

    // ItemJson as the tools return it: the null properties left out, except estimate, the four claim fields and url.
    private const string ItemText =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","title":"Item","type":"Story","status":"Ready","priority":"Medium","order":0,"estimate":null,"labels":"factory","comments":[],"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00","claimant":null,"claimant_id":null,"claim_marker":null,"claim_expires_at_utc":null,"blockers":[],"dependents":[],"links":[],"version":3,"label_list":["factory"],"url":null}""";

    private const string ProjectJson =
        """{"id":"22222222-2222-2222-2222-222222222222","name":"App","key":"APP","description":null,"isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","totalItems":2,"openItems":1,"repository_url":null,"default_branch":"main"}""";

    private const string ProjectText =
        """{"id":"22222222-2222-2222-2222-222222222222","name":"App","key":"APP","isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","totalItems":2,"openItems":1,"repository_url":null,"default_branch":"main"}""";

    private const string BoardStart =
        """{"id":"22222222-2222-2222-2222-222222222222","name":"App","key":"APP","description":"d","isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","items":[""";

    // The project's target, after the fields a board has always had: always written, also as null.
    private const string BoardEnd = ""","repository_url":"https://example.test/r.git","default_branch":null}""";

    private const string EpicJson =
        """{"id":"44444444-4444-4444-4444-444444444444","projectId":"22222222-2222-2222-2222-222222222222","name":"Epic","description":null,"isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00"}""";

    private const string EpicText =
        """{"id":"44444444-4444-4444-4444-444444444444","projectId":"22222222-2222-2222-2222-222222222222","name":"Epic","isArchived":false,"createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00"}""";

    private const string DocumentJson =
        """{"id":"66666666-6666-6666-6666-666666666666","epicId":"44444444-4444-4444-4444-444444444444","title":"Plan","body":"P","createdAtUtc":"2026-10-12T12:00:00+00:00","updatedAtUtc":"2026-10-12T12:00:00+00:00"}""";

    private const string HitJson =
        """{"id":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","title":"Item","type":"Story","status":"Ready","estimate":null,"version":3,"url":null}""";

    // Every tool with every argument it declares. A tool that cannot take all of them in one call has several calls.
    // Array: whether the API answers the tool with a list.
    private static readonly Dictionary<string, (bool Array, Dictionary<string, object?>[] Calls)> Tools = new()
    {
        ["list_projects"] = (true, [new() { ["includeArchived"] = true }]),
        ["create_project"] = (false, [new() { ["name"] = "App", ["key"] = "APP", ["description"] = "d", ["repository_url"] = "https://example.test/r.git", ["default_branch"] = "main", ["idempotency_key"] = "k" }]),
        ["update_project"] = (false,
        [
            new() { ["id"] = Id, ["name"] = "App", ["key"] = "APP", ["description"] = "d", ["repository_url"] = "https://example.test/r.git", ["default_branch"] = "main", ["is_archived"] = false },
            new() { ["id"] = Id, ["clear"] = new[] { "description" } }
        ]),
        ["get_board"] = (false, [new() { ["projectId"] = Id, ["status"] = "Ready", ["includeArchivedEpics"] = true }]),
        ["list_epics"] = (true, [new() { ["projectId"] = Id, ["includeArchived"] = true }]),
        ["get_epic"] = (false, [new() { ["epicId"] = Id }]),
        ["create_epic"] = (false, [new() { ["projectId"] = Id, ["name"] = "Epic", ["description"] = "d", ["isArchived"] = false, ["idempotency_key"] = "k" }]),
        ["update_epic"] = (false, [new() { ["epicId"] = Id, ["name"] = "Epic", ["description"] = "d", ["isArchived"] = true }]),
        ["delete_project"] = (false, [new() { ["projectId"] = Id }]),
        ["delete_epic"] = (false, [new() { ["epicId"] = Id }]),
        ["list_epic_documents"] = (true, [new() { ["epicId"] = Id }]),
        ["get_epic_document"] = (false, [new() { ["documentId"] = Id }]),
        ["create_epic_document"] = (false, [new() { ["epicId"] = Id, ["title"] = "Doc", ["body"] = "b", ["idempotency_key"] = "k" }]),
        ["update_epic_document"] = (false, [new() { ["documentId"] = Id, ["title"] = "Doc", ["body"] = "b" }]),
        ["list_work_items"] = (false,
        [
            new()
            {
                ["projectId"] = Id, ["epicId"] = OtherId, ["type"] = "Story", ["status"] = "Ready", ["includeArchivedEpics"] = true, ["unblocked_only"] = true,
                ["label"] = "api", ["updated_since"] = "2026-10-12T12:00:00Z", ["claim_state"] = "unclaimed", ["limit"] = 10, ["cursor"] = "c", ["include_comments"] = false
            }
        ]),
        ["get_work_item"] = (false, [new() { ["itemId"] = Id }]),
        ["claim_work_item"] = (false, [new() { ["id"] = Id, ["marker"] = "run-1", ["ttl_seconds"] = 60 }]),
        ["renew_work_item_claim"] = (false, [new() { ["id"] = Id, ["marker"] = "run-1", ["ttl_seconds"] = 60 }]),
        ["release_work_item_claim"] = (false, [new() { ["id"] = Id, ["marker"] = "run-1" }]),
        ["add_work_item_dependency"] = (false, [new() { ["id"] = Id, ["blocked_by_id"] = OtherId }]),
        ["remove_work_item_dependency"] = (false, [new() { ["id"] = Id, ["blocked_by_id"] = OtherId }]),
        ["add_work_item_link"] = (false, [new() { ["id"] = Id, ["kind"] = "pr", ["url"] = "https://example.test/pr/1" }]),
        ["remove_work_item_link"] = (false, [new() { ["id"] = Id, ["link_id"] = OtherId, ["kind"] = "pr", ["url"] = "https://example.test/pr/1" }]),
        ["find_work_items_by_link"] = (true, [new() { ["url"] = "https://example.test/pr/1" }]),
        ["search_work_items"] = (true, [new() { ["query"] = "needle", ["projectId"] = Id, ["limit"] = 5 }]),
        ["list_epic_work_items"] = (true, [new() { ["epicId"] = Id, ["type"] = "Task", ["status"] = "Done", ["includeArchivedEpics"] = true, ["unblocked_only"] = true }]),
        ["get_issues"] = (true, [new() { ["projectId"] = Id, ["status"] = "Ready", ["unblocked_only"] = true }]),
        ["add_work_item_comment"] = (false, [new() { ["itemId"] = Id, ["body"] = "note", ["author"] = "claude", ["idempotency_key"] = "k" }]),
        ["add_issue_comment"] = (false, [new() { ["itemId"] = Id, ["author"] = "claude", ["body"] = "note", ["idempotency_key"] = "k" }]),
        ["list_work_item_comments"] = (false, [new() { ["id"] = Id, ["limit"] = 10, ["cursor"] = "c" }]),
        ["list_changes"] = (false, [new() { ["cursor"] = "c", ["project_id"] = Id, ["limit"] = 10, ["from_start"] = false }]),
        ["create_work_item"] = (false,
        [
            new()
            {
                ["projectId"] = Id, ["title"] = "Item", ["type"] = "Task", ["status"] = "Ready", ["priority"] = "High", ["epicId"] = OtherId, ["description"] = "d",
                ["estimate"] = 3, ["labels"] = "a,b", ["label_list"] = new[] { "a" }, ["idempotency_key"] = "k"
            }
        ]),
        ["create_epic_work_item"] = (false,
        [
            new()
            {
                ["epicId"] = Id, ["title"] = "Item", ["type"] = "Task", ["status"] = "Ready", ["priority"] = "High", ["description"] = "d",
                ["estimate"] = 3, ["labels"] = "a,b", ["label_list"] = new[] { "a" }, ["idempotency_key"] = "k"
            }
        ]),
        ["import_epic_plan"] = (false,
        [
            new()
            {
                ["project_id"] = Id, ["idempotency_key"] = "plan-1", ["epic"] = new { name = "Checkout" },
                ["documents"] = new[] { new { title = "Plan", body = "P" } }, ["items"] = new object[] { new { @ref = "A", title = "First" } }
            }
        ]),
        ["move_work_item"] = (false, [new() { ["itemId"] = Id, ["status"] = "Done", ["order"] = 2, ["expected_version"] = 3 }]),
        ["update_work_item"] = (false,
        [
            new()
            {
                ["itemId"] = Id, ["title"] = "Item", ["type"] = "Task", ["status"] = "Ready", ["priority"] = "High", ["epicId"] = OtherId, ["description"] = "d",
                ["estimate"] = 3, ["labels"] = "a,b", ["expected_version"] = 3
            },
            new() { ["itemId"] = Id, ["label_list"] = new[] { "a" }, ["clear"] = new[] { "description" } }
        ]),
        ["delete_work_item"] = (false, [new() { ["itemId"] = Id }])
    };

    private static HttpResponseMessage Answer(string tool, RecordedRequest request) =>
        Json(HttpStatusCode.OK, Tools[tool].Array ? "[]" : "{}");

    [Fact]
    public async Task Every_tool_is_callable_with_every_argument_it_declares()
    {
        var current = string.Empty;
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, request => Answer(current, request));

        var listed = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        Assert.Equal(listed.Keys.Order(), Tools.Keys.Order());
        foreach (var (tool, (_, calls)) in Tools)
        {
            current = tool;
            // The calls together pass every argument the tool lists, spelt as it lists them.
            Assert.Equal(ArgumentNames(listed[tool]), calls.SelectMany(call => call.Keys).Distinct().Order().ToArray());
            foreach (var call in calls)
            {
                var before = server.Api.Requests.Count;
                var result = await server.CallAsync(tool, call);

                Assert.True(result.IsError != true, $"{tool}: {Text(result)}");
                Assert.True(server.Api.Requests.Count > before, tool);
            }
        }
    }

    public static TheoryData<string, string> MisspeltArguments => new()
    {
        // documents, misspelt: the plan would be imported without its documents, and the key spent.
        { "import_epic_plan", "document" },
        // The guard of a guarded write, misspelt: the update would be written unguarded.
        { "update_work_item", "expected_versoin" },
        { "move_work_item", "expectedVersion" },
        // Argument names are compared exactly: a filter spelt in another case would be dropped and everything returned.
        { "list_work_items", "projectid" },
        { "get_work_item", "include_comments" }
    };

    [Theory]
    [MemberData(nameof(MisspeltArguments))]
    public async Task An_argument_the_tool_does_not_declare_is_refused_invalid_argument_and_nothing_is_called(string tool, string misspelt)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, request => Answer(tool, request));
        var call = new Dictionary<string, object?>(Tools[tool].Calls[0]);
        if (tool == "import_epic_plan")
        {
            // The call the review made: documents as document, and an argument that is nothing at all.
            call.Remove("documents", out var documents);
            call[misspelt] = documents;
            call["itemz"] = 1;
        }
        else
        {
            call[misspelt] = 3;
        }

        var result = await server.CallAsync(tool, call);

        var message = AssertError(result, KanbanErrorCodes.InvalidArgument);
        Assert.Contains($"{tool} has no argument '{misspelt}'", message, StringComparison.Ordinal);
        // The message lists the arguments the tool does have.
        var declared = ArgumentNames((await server.Client.ListToolsAsync()).Single(listed => listed.Name == tool));
        Assert.All(declared, name => Assert.Contains(name, message, StringComparison.Ordinal));
        Assert.Empty(server.Api.Requests);
    }

    [Fact]
    public async Task A_read_scope_token_is_told_of_a_misspelt_argument_of_a_read_tool_and_a_well_spelt_call_still_reads()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, ItemJson));

        var refused = await server.CallAsync("get_work_item", new() { ["itemId"] = Id, ["itemid"] = Id });
        var read = await server.CallAsync("get_work_item", new() { ["itemId"] = Id });

        AssertError(refused, KanbanErrorCodes.InvalidArgument);
        Assert.Equal(ItemText, Text(read));
        Assert.Single(server.Api.Requests);
    }

    public static TheoryData<string> WriteToolNames => new(KanbanTools.WriteToolNames);

    [Fact]
    public async Task The_write_tools_the_server_checks_the_scope_of_first_are_exactly_the_tools_that_ask_for_the_write_scope()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, request => Answer("get_epic", request));
        var listed = (await server.Client.ListToolsAsync()).Select(tool => tool.Name).ToList();
        // create_work_item for CreateWorkItem: the name the server lists a method under.
        var asked = KanbanToolsScopeTests.WriteTools.Select(method => System.Text.RegularExpressions.Regex.Replace(method, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());

        Assert.Equal(asked.Order(), KanbanTools.WriteToolNames.Order());
        Assert.Subset(listed.ToHashSet(), KanbanTools.WriteToolNames.ToHashSet());
        Assert.Equal(23, KanbanTools.WriteToolNames.Count);
        Assert.Equal(37, listed.Count);
    }

    // What is wrong with the arguments of a call, three ways: one the tool does not have, a required one left out,
    // and one of a type the tool does not take.
    private static Dictionary<string, object?>[] MalformedCalls(string tool)
    {
        var call = Tools[tool].Calls[0];
        return
        [
            new(call) { ["no_such_argument"] = 1 },
            [],
            new(call) { [call.Keys.First()] = new[] { new { not = "an id" } } }
        ];
    }

    [Theory]
    [MemberData(nameof(WriteToolNames))]
    public async Task A_read_scope_token_is_refused_scope_denied_by_a_write_tool_whatever_is_wrong_with_the_arguments(string tool)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, request => Answer(tool, request));

        foreach (var call in MalformedCalls(tool).Append(Tools[tool].Calls[0]))
        {
            var result = await server.CallAsync(tool, call);

            Assert.Equal(KanbanTools.ReadOnlyScopeErrorMessage, AssertError(result, KanbanErrorCodes.ScopeDenied));
        }

        Assert.Empty(server.Api.Requests);
    }

    [Theory]
    [MemberData(nameof(WriteToolNames))]
    public async Task A_full_scope_token_is_told_invalid_argument_for_the_same_calls_and_nothing_is_called(string tool)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, request => Answer(tool, request));

        foreach (var call in MalformedCalls(tool))
        {
            var result = await server.CallAsync(tool, call);

            AssertError(result, KanbanErrorCodes.InvalidArgument);
        }

        Assert.Empty(server.Api.Requests);
    }

    // An exception of the kinds a malformed argument raises, thrown once the body of the tool runs: the call may have
    // written, so it is a fault of the server and must not be answered as "nothing was called".
    public static TheoryData<string, string> FaultsAfterTheBodyStarted
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var tool in Tools.Keys)
            {
                data.Add(tool, "json");
                data.Add(tool, "argument");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FaultsAfterTheBodyStarted))]
    public async Task A_json_or_argument_exception_thrown_once_the_tool_runs_is_not_answered_invalid_argument(string tool, string kind)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ =>
            throw (kind == "json" ? new JsonException("after the body started") : new ArgumentException("after the body started")));

        var result = await server.CallAsync(tool, Tools[tool].Calls[0]);

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.DoesNotContain(KanbanErrorCodes.InvalidArgument, Text(result), StringComparison.Ordinal);
        // The tool was running: it had reached the API.
        Assert.NotEmpty(server.Api.Requests);
    }

    [Fact]
    public async Task An_argument_that_cannot_be_bound_is_answered_invalid_argument_and_nothing_is_called()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, ItemJson));

        var missing = await server.CallAsync("move_work_item", new() { ["status"] = "Done" });
        var wrongType = await server.CallAsync("move_work_item", new() { ["itemId"] = Id, ["status"] = "Done", ["order"] = "first" });
        var readMissing = await server.CallAsync("get_epic", []);

        Assert.Contains("move_work_item", AssertError(missing, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
        Assert.Contains("move_work_item", AssertError(wrongType, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
        Assert.Contains("get_epic", AssertError(readMissing, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
        Assert.Empty(server.Api.Requests);
    }

    // The read tools that answered an API refusal with an error without a code.
    public static TheoryData<string, HttpStatusCode, string, string, string> ReadRefusals => new()
    {
        { "get_epic", HttpStatusCode.NotFound, "", KanbanErrorCodes.NotFound, $"Epic {Id} was not found." },
        { "get_epic_document", HttpStatusCode.NotFound, "", KanbanErrorCodes.NotFound, $"Epic document {Id} was not found." },
        { "get_board", HttpStatusCode.NotFound, "", KanbanErrorCodes.NotFound, $"Project {Id} was not found." },
        { "get_work_item", HttpStatusCode.NotFound, "", KanbanErrorCodes.NotFound, $"Work item {Id} was not found." },
        { "list_epics", HttpStatusCode.NotFound, """{"message":"Project not found."}""", KanbanErrorCodes.NotFound, "Project not found." },
        { "list_epic_documents", HttpStatusCode.NotFound, """{"message":"Epic not found."}""", KanbanErrorCodes.NotFound, "Epic not found." },
        { "search_work_items", HttpStatusCode.BadRequest, """{"code":"invalid_argument","message":"Argument 'query' is required."}""", KanbanErrorCodes.InvalidArgument, "Argument 'query' is required." },
        { "list_projects", HttpStatusCode.Forbidden, """{"code":"scope_denied","message":"revoked"}""", KanbanErrorCodes.ScopeDenied, "revoked" },
        { "get_work_item", HttpStatusCode.Forbidden, """{"code":"scope_denied","message":"revoked"}""", KanbanErrorCodes.ScopeDenied, "revoked" },
        { "get_board", HttpStatusCode.ServiceUnavailable, """{"code":"busy","message":"later"}""", KanbanErrorCodes.Busy, "later" }
    };

    [Theory]
    [MemberData(nameof(ReadRefusals))]
    public async Task A_refusal_of_a_read_is_returned_with_a_code(string tool, HttpStatusCode status, string apiBody, string code, string message)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(status, apiBody));
        var call = Tools[tool].Calls[0].Where(argument => argument.Key is "projectId" or "epicId" or "documentId" or "itemId" or "query")
            .ToDictionary(argument => argument.Key, argument => argument.Value);

        var result = await server.CallAsync(tool, call);

        Assert.Equal(message, AssertError(result, code));
    }

    [Fact]
    public async Task A_blank_search_is_refused_invalid_argument_without_calling_the_api()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, "[]"));

        var result = await server.CallAsync("search_work_items", new() { ["query"] = "  " });

        AssertError(result, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(server.Api.Requests);
    }

    [Fact]
    public async Task A_failure_of_the_api_that_is_not_a_refusal_stays_a_fault_without_a_code()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.InternalServerError, ""));

        var result = await server.CallAsync("get_epic", new() { ["epicId"] = Id });

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
    }

    // The read tools from before the factory work: what each returns for an answer of the API, byte for byte. The
    // null properties of the answer are left out, except the ones that are always written.
    public static TheoryData<string, string, string, string> ReadResults => new()
    {
        { "list_projects", "/api/projects?includeArchived=False", $"[{ProjectJson}]", $"[{ProjectText}]" },
        { "get_board", $"/api/projects/{Id}", $"{BoardStart}{ItemJson}]{BoardEnd}", $"{BoardStart}{ItemText}]{BoardEnd}" },
        { "list_epics", $"/api/projects/{Id}/epics?includeArchived=False", $"[{EpicJson}]", $"[{EpicText}]" },
        { "get_epic", $"/api/epics/{Id}", EpicJson, EpicText },
        { "list_epic_documents", $"/api/epics/{Id}/documents", $"[{DocumentJson}]", $"[{DocumentJson}]" },
        { "get_epic_document", $"/api/epic-documents/{Id}", DocumentJson, DocumentJson },
        { "list_work_items", "/api/items", $"[{ItemJson}]", $"[{ItemText}]" },
        { "get_work_item", $"/api/items/{Id}", ItemJson, ItemText },
        { "search_work_items", "/api/items/search?query=needle", $"[{HitJson}]", $"[{HitJson}]" },
        { "list_epic_work_items", $"/api/items?epicId={Id}", $"[{ItemJson}]", $"[{ItemText}]" },
        { "get_issues", "/api/items?type=Issue", $"[{ItemJson}]", $"[{ItemText}]" }
    };

    [Theory]
    [MemberData(nameof(ReadResults))]
    public async Task A_read_tool_from_before_the_factory_work_sends_the_request_and_returns_the_text_it_always_did(string tool, string request, string apiBody, string text)
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Read, _ => Json(HttpStatusCode.OK, apiBody));
        var call = Tools[tool].Calls[0].Where(argument => argument.Key is "projectId" or "epicId" or "documentId" or "itemId" or "query")
            .ToDictionary(argument => argument.Key, argument => argument.Value);
        if (tool is "list_work_items" or "search_work_items" or "get_issues")
        {
            call.Remove("projectId");
            call.Remove("epicId");
        }

        var result = await server.CallAsync(tool, call);

        Assert.NotEqual(true, result.IsError);
        var sent = Assert.Single(server.Api.Requests);
        Assert.Equal((HttpMethod.Get, request), (sent.Method, sent.Path + sent.Query));
        Assert.Equal(text, Text(result));
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
