using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using static KanbanBoard.Tests.ProgramDependencyWiringTests;

namespace KanbanBoard.Tests;

// The import of an epic plan through the real application (Program.cs) on SQLite on a file: real SQL, real unique
// indexes and foreign keys, one connection per request.
public sealed class EpicPlanImportApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // C is blocked by B and B by A; all three are Ready.
    private static object ThreeItems(string key = "plan-1") => new
    {
        idempotency_key = key,
        epic = new { name = " Checkout ", description = " d " },
        documents = new[] { new { title = "Requirements", body = "R" }, new { title = "Plan", body = "P" } },
        items = new object[]
        {
            new { @ref = "A", title = "First", status = "Ready" },
            new { @ref = "B", title = "Second", status = "Ready", type = "Task", priority = "High", estimate = 3, label_list = new[] { "api", "ui" }, blocked_by = new[] { "A" } },
            new { @ref = "C", title = "Third", status = "Ready", description = " last ", labels = "db", blocked_by = new[] { "B" } }
        }
    };

    [Fact]
    public async Task One_import_creates_the_epic_its_documents_its_items_and_both_dependencies_and_returns_their_ids()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);
        var head = await HeadAsync(client);

        using var response = await client.PostAsJsonAsync($"/api/projects/{project}/epic-plans", ThreeItems());

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, raw);
        var result = JsonSerializer.Deserialize<ImportEpicPlanResultDto>(raw, Json)!;
        Assert.True(result.Created);
        Assert.Equal(project, result.ProjectId);
        Assert.Equal(["A", "B", "C"], result.ItemIds.Keys.ToArray());
        Guid a = result.ItemIds["A"], b = result.ItemIds["B"], c = result.ItemIds["C"];

        using var dbContext = api.OpenDatabase();
        var epic = await dbContext.Epics.SingleAsync();
        Assert.Equal((result.EpicId, project, "Checkout", "d", false), (epic.Id, epic.ProjectId, epic.Name, epic.Description, epic.IsArchived));
        var documents = await client.GetFromJsonAsync<List<EpicDocumentDto>>($"/api/epics/{epic.Id}/documents", Json);
        Assert.Equal(result.DocumentIds, documents!.Select(document => document.Id).ToArray());
        Assert.Equal([("Requirements", "R"), ("Plan", "P")], documents!.Select(document => (document.Title, document.Body)).ToArray());

        var items = (await dbContext.WorkItems.ToListAsync()).OrderBy(item => item.Order).ToList();
        Assert.Equal([a, b, c], items.Select(item => item.Id).ToArray());
        Assert.All(items, item => Assert.Equal((project, epic.Id, WorkItemStatus.Ready, 1L, null), (item.ProjectId, item.EpicId, item.Status, item.Version, item.IdempotencyKeyHash)));
        Assert.Equal([0, 1, 2], items.Select(item => item.Order).ToArray());
        Assert.Equal(
            [("First", WorkItemType.Story, WorkItemPriority.Medium, null, null, null), ("Second", WorkItemType.Task, WorkItemPriority.High, 3, "api,ui", null), ("Third", WorkItemType.Story, WorkItemPriority.Medium, null, "db", "last")],
            items.Select(item => (item.Title, item.Type, item.Priority, item.Estimate, item.Labels, item.Description)).ToArray());
        var edges = await dbContext.WorkItemDependencies.ToListAsync();
        Assert.Equal(new[] { (b, a), (c, b) }.Order().ToArray(), edges.Select(edge => (edge.BlockedItemId, edge.BlockerItemId)).Order().ToArray());

        // The feed: the items in the order of the plan, then the dependencies, all by the token's identity.
        var entries = (await ReadFeedAsync(client, head)).Items;
        Assert.Equal(
            [("item_created", a), ("item_created", b), ("item_created", c), ("dependency_added", b), ("dependency_added", c)],
            entries.Select(entry => (entry.Kind, entry.ItemId)).ToArray());
        Assert.All(entries, entry => Assert.Equal((project, "Factory"), (entry.ProjectId, entry.Actor!.Name)));
        Assert.Contains($"\"field\":\"blocked_by\",\"old\":null,\"new\":\"{a}\"", entries[3].Changes.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"field\":\"status\",\"old\":null,\"new\":\"Ready\"", entries[0].Changes.GetRawText(), StringComparison.Ordinal);
        Assert.Contains($"\"field\":\"epic_id\",\"old\":null,\"new\":\"{epic.Id}\"", entries[0].Changes.GetRawText(), StringComparison.Ordinal);

        // What the factory asks next: the ready, unblocked items are A alone.
        var ready = await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items?projectId={project}&status=Ready&unblockedOnly=true", Json);
        Assert.Equal([a], ready!.Select(item => item.Id).ToArray());
        // Oldest first is the order of the plan.
        var page = await client.GetFromJsonAsync<WorkItemPageDto>($"/api/items?projectId={project}&limit=10", Json);
        Assert.Equal([a, b, c], page!.Items.Select(item => item.Id).ToArray());
        Assert.Equal([b], page.Items[2].Blockers.Select(blocker => blocker.Id).ToArray());
    }

    public static TheoryData<string, string, string> RefusedPlans => new()
    {
        { "an unknown blocked_by", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a"},{"ref":"B","title":"b","blocked_by":["Z"]}]}""" },
        { "a ref in another case", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a"},{"ref":"B","title":"b","blocked_by":["a"]}]}""" },
        { "a cycle", KanbanErrorCodes.Cycle, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","blocked_by":["C"]},{"ref":"B","title":"b","blocked_by":["A"]},{"ref":"C","title":"c","blocked_by":["B"]}]}""" },
        { "an item blocked by itself", KanbanErrorCodes.Cycle, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","blocked_by":["A"]}]}""" },
        { "an item without a title", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a"},{"ref":"B","title":" "}]}""" },
        { "a title that is too long", KanbanErrorCodes.InvalidArgument, $$"""{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"{{new string('t', 241)}}"}]}""" },
        { "an unknown status", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","status":"Started"}]}""" },
        { "a status given as a number", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","status":"1"}]}""" },
        { "an unknown type", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","type":"Bug"}]}""" },
        { "an unknown priority", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","priority":"Urgent"}]}""" },
        { "labels given twice", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","labels":"x","label_list":["y"]}]}""" },
        { "a label with a comma", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","label_list":["x,y"]}]}""" },
        { "an estimate that is not a number", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","estimate":"five"}]}""" },
        { "a ref used twice", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a"},{"ref":"A","title":"b"}]}""" },
        { "an item without a ref", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"title":"a"}]}""" },
        { "a ref that is too long", KanbanErrorCodes.InvalidArgument, $$"""{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"{{new string('r', 65)}}","title":"a"}]}""" },
        { "a misspelt field of an item", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","blockedBy":["A"]}]}""" },
        { "a misspelt field of the epic", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E","title":"E"},"items":[{"ref":"A","title":"a"}]}""" },
        { "a misspelt field of the plan", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"item":[],"items":[{"ref":"A","title":"a"}]}""" },
        { "a null item", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[null]}""" },
        { "no items", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"items":[]}""" },
        { "items left out", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"}}""" },
        { "no epic", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","items":[{"ref":"A","title":"a"}]}""" },
        { "an epic without a name", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":""},"items":[{"ref":"A","title":"a"}]}""" },
        { "an epic name that is too long", KanbanErrorCodes.InvalidArgument, $$"""{"idempotency_key":"k","epic":{"name":"{{new string('n', 161)}}"},"items":[{"ref":"A","title":"a"}]}""" },
        { "a document without a body", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"k","epic":{"name":"E"},"documents":[{"title":"D","body":""}],"items":[{"ref":"A","title":"a"}]}""" },
        { "a document title that is too long", KanbanErrorCodes.InvalidArgument, $$"""{"idempotency_key":"k","epic":{"name":"E"},"documents":[{"title":"{{new string('t', 201)}}","body":"b"}],"items":[{"ref":"A","title":"a"}]}""" },
        { "no idempotency key", KanbanErrorCodes.InvalidArgument, """{"epic":{"name":"E"},"items":[{"ref":"A","title":"a"}]}""" },
        { "a key with a space", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":"plan 1","epic":{"name":"E"},"items":[{"ref":"A","title":"a"}]}""" },
        { "a body that is not JSON", KanbanErrorCodes.InvalidArgument, """{"idempotency_key":""" },
        { "a body that is null", KanbanErrorCodes.InvalidArgument, "null" },
        { "more items than a plan may hold", KanbanErrorCodes.InvalidArgument, Plan(items: EpicPlanLimits.MaxItems + 1) },
        { "more documents than a plan may hold", KanbanErrorCodes.InvalidArgument, Plan(items: 1, documents: EpicPlanLimits.MaxDocuments + 1) },
        { "more dependencies than a plan may hold", KanbanErrorCodes.InvalidArgument, Plan(items: 60, blockedByEarlier: 10) },
        { "more bytes than a plan may hold", KanbanErrorCodes.InvalidArgument, Plan(items: 1, description: new string('d', EpicPlanLimits.MaxRequestBytes)) }
    };

    [Theory]
    [MemberData(nameof(RefusedPlans))]
    public async Task A_plan_that_is_refused_creates_nothing(string what, string code, string plan)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);
        var before = await CountsAsync(api);

        using var response = await client.PostAsync($"/api/projects/{project}/epic-plans", new StringContent(plan, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{what}: {response.StatusCode} {body}");
        var error = JsonSerializer.Deserialize<KanbanErrorDto>(body, Json)!;
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(before, await CountsAsync(api));
    }

    [Fact]
    public async Task The_refusal_names_the_item_and_the_fault()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);

        async Task<string> MessageAsync(string plan)
        {
            using var response = await client.PostAsync($"/api/projects/{project}/epic-plans", new StringContent(plan, Encoding.UTF8, "application/json"));
            return (await response.Content.ReadFromJsonAsync<KanbanErrorDto>(Json))!.Message;
        }

        Assert.Equal(
            "Item 'B' (items[1]): blocked_by names 'Z', which is not the ref of an item of the plan.",
            await MessageAsync("""{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a"},{"ref":"B","title":"b","blocked_by":["Z"]}]}"""));
        Assert.Equal(
            "Item 'A' (items[0]) has no field 'blockedBy'. Its fields are ref, title, type, status, priority, description, estimate, labels, label_list and blocked_by.",
            await MessageAsync("""{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","blockedBy":[]}]}"""));
        Assert.StartsWith(
            "Item 'C' blocked by 'B' would make an item of the plan block itself",
            await MessageAsync("""{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","blocked_by":["C"]},{"ref":"B","title":"b","blocked_by":["A"]},{"ref":"C","title":"c","blocked_by":["B"]}]}"""),
            StringComparison.Ordinal);
        Assert.Equal(
            "Item 'A' (items[0]): Field 'status' must be one of Backlog, Ready, InProgress, Blocked, Done, Closed.",
            await MessageAsync("""{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"a","status":"Started"}]}"""));
        Assert.Equal(
            $"Item 'A' (items[0]): Argument 'title' must be at most {WorkItemLimits.MaxTitleLength} characters.",
            await MessageAsync($$"""{"idempotency_key":"k","epic":{"name":"E"},"items":[{"ref":"A","title":"{{new string('t', 241)}}"}]}"""));
    }

    [Fact]
    public async Task An_import_into_a_project_that_does_not_exist_is_not_found_and_creates_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        await CreateProjectAsync(client);
        var before = await CountsAsync(api);

        using var response = await client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/epic-plans", ThreeItems());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(KanbanErrorCodes.NotFound, (await response.Content.ReadFromJsonAsync<KanbanErrorDto>(Json))!.Code);
        Assert.Equal(before, await CountsAsync(api));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_import_that_fails_while_it_is_stored_leaves_nothing_behind_and_can_be_repeated(bool whileRecordingTheFeed)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);
        var before = await CountsAsync(api);
        if (whileRecordingTheFeed)
        {
            api.FailNextChangeLogSave = true;
        }
        else
        {
            api.FailNextSave = true;
        }

        using var failed = await client.PostAsJsonAsync($"/api/projects/{project}/epic-plans", ThreeItems());

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        // The epic, the documents, the items and the dependencies were all sent before the feed's entries: none stays.
        Assert.Equal(before, await CountsAsync(api));

        var repeated = await ImportAsync(client, project, ThreeItems());
        Assert.True(repeated.Created);
        Assert.Equal(before with { Epics = 1, Documents = 2, Items = 3, Dependencies = 2, Imports = 1, FeedEntries = before.FeedEntries + 5 }, await CountsAsync(api));
    }

    [Fact]
    public async Task A_repeat_of_the_idempotency_key_returns_the_same_ids_and_creates_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);
        var first = await ImportAsync(client, project, ThreeItems());
        var after = await CountsAsync(api);
        var raw = await client.GetStringAsync("/api/items/changes");

        using var repeatResponse = await client.PostAsJsonAsync($"/api/projects/{project}/epic-plans", ThreeItems());
        // The key names the import: a repeat with another plan still answers with the first one.
        using var otherResponse = await client.PostAsJsonAsync($"/api/projects/{project}/epic-plans", new
        {
            idempotency_key = "plan-1", epic = new { name = "Other" }, items = new[] { new { @ref = "X", title = "x" } }
        });

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (repeatResponse.StatusCode, otherResponse.StatusCode));
        foreach (var response in new[] { repeatResponse, otherResponse })
        {
            var repeat = (await response.Content.ReadFromJsonAsync<ImportEpicPlanResultDto>(Json))!;
            Assert.False(repeat.Created);
            Assert.Equal((first.ProjectId, first.EpicId), (repeat.ProjectId, repeat.EpicId));
            Assert.Equal(first.DocumentIds, repeat.DocumentIds);
            Assert.Equal(first.ItemIds.ToArray(), repeat.ItemIds.ToArray());
        }

        Assert.True(first.Created);
        Assert.Equal(after, await CountsAsync(api));
        // The head of the feed has not moved.
        Assert.Equal(raw, await client.GetStringAsync("/api/items/changes"));
    }

    [Fact]
    public async Task A_repeat_returns_the_ids_of_the_import_whatever_happened_to_its_objects_since()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);
        var first = await ImportAsync(client, project, ThreeItems());
        Guid a = first.ItemIds["A"], b = first.ItemIds["B"], c = first.ItemIds["C"];

        (await client.PostAsJsonAsync($"/api/items/{a}/move", new { status = "Done", order = 0 })).EnsureSuccessStatusCode();
        (await client.PatchAsJsonAsync($"/api/items/{b}", new { title = "Renamed" })).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/items/{c}")).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/items/{b}/dependencies/{a}")).EnsureSuccessStatusCode();
        // Archiving the epic takes its unfinished items out of it.
        (await client.PutAsJsonAsync($"/api/epics/{first.EpicId}", new { name = "Archived", isArchived = true })).EnsureSuccessStatusCode();
        var after = await CountsAsync(api);

        var repeat = await ImportAsync(client, project, ThreeItems());

        Assert.False(repeat.Created);
        Assert.Equal(first.EpicId, repeat.EpicId);
        Assert.Equal(first.DocumentIds, repeat.DocumentIds);
        // The ids as the import created them, the deleted item's included: the answer is not rebuilt from the board.
        Assert.Equal(first.ItemIds.ToArray(), repeat.ItemIds.ToArray());
        Assert.Equal(after, await CountsAsync(api));
        using var dbContext = api.OpenDatabase();
        Assert.Equal("Renamed", (await dbContext.WorkItems.SingleAsync(item => item.Id == b)).Title);
    }

    [Fact]
    public async Task A_key_belongs_to_one_identity_and_one_project_and_goes_with_the_epic()
    {
        using var api = new ApiApplication();
        using var factory = api.CreateCallerClient("Factory");
        using var other = api.CreateCallerClient("Other");
        var project = await CreateProjectAsync(factory);
        var second = await CreateProjectAsync(factory, "SECOND");

        var first = await ImportAsync(factory, project, ThreeItems());
        var byOther = await ImportAsync(other, project, ThreeItems());
        var inSecond = await ImportAsync(factory, second, ThreeItems());
        var otherCase = await ImportAsync(factory, project, ThreeItems("PLAN-1"));

        Assert.All(new[] { first, byOther, inSecond, otherCase }, result => Assert.True(result.Created));
        Assert.Equal(4, new[] { first, byOther, inSecond, otherCase }.Select(result => result.EpicId).Distinct().Count());
        using (var dbContext = api.OpenDatabase())
        {
            var user = await dbContext.AppUsers.SingleAsync(candidate => candidate.DisplayName == "Factory");
            // The key itself is not stored.
            Assert.Equal(IdempotencyKeys.Hash(user.Id, "plan-1"), (await dbContext.EpicPlanImports.SingleAsync(import => import.EpicId == first.EpicId)).IdempotencyKeyHash);
        }

        // Deleting the epic frees its key: the plan is imported again.
        (await factory.DeleteAsync($"/api/epics/{first.EpicId}")).EnsureSuccessStatusCode();
        var again = await ImportAsync(factory, project, ThreeItems());
        Assert.True(again.Created);
        Assert.NotEqual(first.EpicId, again.EpicId);
    }

    [Fact]
    public async Task Imported_items_go_to_the_end_of_their_columns_in_the_order_of_the_plan()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);
        (await client.PostAsJsonAsync("/api/items", new { projectId = project, title = "Existing", type = "Task", status = "Ready", priority = "Medium" })).EnsureSuccessStatusCode();

        var first = await ImportAsync(client, project, new
        {
            idempotency_key = "one", epic = new { name = "One" },
            items = new object[] { new { @ref = "r1", title = "r1", status = "Ready" }, new { @ref = "b1", title = "b1" }, new { @ref = "r2", title = "r2", status = "ready" } }
        });
        var second = await ImportAsync(client, project, new
        {
            idempotency_key = "two", epic = new { name = "Two" },
            items = new object[] { new { @ref = "b2", title = "b2", status = "Backlog" }, new { @ref = "r3", title = "r3", status = "Ready" } }
        });

        using var dbContext = api.OpenDatabase();
        var orders = (await dbContext.WorkItems.ToListAsync()).ToDictionary(item => item.Id, item => (item.Status, item.Order));
        Assert.Equal((WorkItemStatus.Ready, 1), orders[first.ItemIds["r1"]]);
        Assert.Equal((WorkItemStatus.Ready, 2), orders[first.ItemIds["r2"]]);
        Assert.Equal((WorkItemStatus.Ready, 3), orders[second.ItemIds["r3"]]);
        Assert.Equal((WorkItemStatus.Backlog, 0), orders[first.ItemIds["b1"]]);
        Assert.Equal((WorkItemStatus.Backlog, 1), orders[second.ItemIds["b2"]]);
    }

    [Fact]
    public async Task Imports_with_one_key_at_the_same_moment_import_once_and_all_report_the_same_ids()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => client.PostAsJsonAsync($"/api/projects/{project}/epic-plans", ThreeItems()))));

        var results = new List<ImportEpicPlanResultDto>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            results.Add(JsonSerializer.Deserialize<ImportEpicPlanResultDto>(body, Json)!);
        }

        Assert.Single(results, result => result.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(results.Select(result => result.EpicId).Distinct());
        Assert.Single(results.Select(result => string.Join(",", result.ItemIds.OrderBy(pair => pair.Key).Select(pair => pair.Value))).Distinct());
        var counts = await CountsAsync(api);
        Assert.Equal((1, 2, 3, 2, 1, 5), (counts.Epics, counts.Documents, counts.Items, counts.Dependencies, counts.Imports, counts.FeedEntries));
    }

    [Fact]
    public async Task A_plan_of_the_largest_size_is_imported_whole()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var project = await CreateProjectAsync(client);

        // 200 items, 20 documents and 500 dependencies: every item from the 12th on is blocked by earlier ones.
        using var response = await client.PostAsync(
            $"/api/projects/{project}/epic-plans",
            new StringContent(Plan(items: EpicPlanLimits.MaxItems, documents: EpicPlanLimits.MaxDocuments, dependencies: EpicPlanLimits.MaxDependencies), Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        var counts = await CountsAsync(api);
        Assert.Equal(
            (1, EpicPlanLimits.MaxDocuments, EpicPlanLimits.MaxItems, EpicPlanLimits.MaxDependencies, EpicPlanLimits.MaxItems + EpicPlanLimits.MaxDependencies),
            (counts.Epics, counts.Documents, counts.Items, counts.Dependencies, counts.FeedEntries));
    }

    internal sealed record Counts(int Projects, int Epics, int Documents, int Items, int Dependencies, int Comments, int Links, int Imports, int FeedEntries);

    internal static async Task<Counts> CountsAsync(ApiApplication api)
    {
        using var dbContext = api.OpenDatabase();
        return new(
            await dbContext.Projects.CountAsync(),
            await dbContext.Epics.CountAsync(),
            await dbContext.EpicDocuments.CountAsync(),
            await dbContext.WorkItems.CountAsync(),
            await dbContext.WorkItemDependencies.CountAsync(),
            await dbContext.WorkItemComments.CountAsync(),
            await dbContext.WorkItemLinks.CountAsync(),
            await dbContext.EpicPlanImports.CountAsync(),
            await dbContext.WorkItemChanges.CountAsync());
    }

    /// <summary>
    /// A plan of <paramref name="items"/> items. With <paramref name="blockedByEarlier"/> every item is blocked by that
    /// many of the items before it (as far as there are any); with <paramref name="dependencies"/> the plan holds
    /// exactly that many dependencies.
    /// </summary>
    internal static string Plan(int items, int documents = 0, int blockedByEarlier = 0, int? dependencies = null, string? description = null, string key = "k")
    {
        var remaining = dependencies ?? int.MaxValue;
        var perItem = dependencies is null ? blockedByEarlier : 3;
        return JsonSerializer.Serialize(new
        {
            idempotency_key = key,
            epic = new { name = "Epic" },
            documents = Enumerable.Range(0, documents).Select(index => new { title = $"Document {index}", body = "body" }),
            items = Enumerable.Range(0, items).Select(index =>
            {
                var blockers = Enumerable.Range(Math.Max(0, index - perItem), Math.Min(index, perItem)).Take(remaining).Select(blocker => $"item-{blocker}").ToArray();
                remaining -= blockers.Length;
                return new { @ref = $"item-{index}", title = $"Item {index}", description, status = "Ready", blocked_by = blockers };
            }).ToArray()
        });
    }

    internal static async Task<Guid> CreateProjectAsync(HttpClient client, string key = "PLAN")
    {
        using var response = await client.PostAsJsonAsync("/api/projects", new { name = "Project", key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectSummaryDto>(Json))!.Id;
    }

    private static async Task<ImportEpicPlanResultDto> ImportAsync(HttpClient client, Guid project, object plan)
    {
        using var response = await client.PostAsJsonAsync($"/api/projects/{project}/epic-plans", plan);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<ImportEpicPlanResultDto>(body, Json)!;
    }

    private static async Task<string> HeadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<WorkItemChangePageDto>("/api/items/changes", Json))!.NextCursor;

    private static async Task<WorkItemChangePageDto> ReadFeedAsync(HttpClient client, string cursor) =>
        (await client.GetFromJsonAsync<WorkItemChangePageDto>($"/api/items/changes?cursor={Uri.EscapeDataString(cursor)}&limit=200", Json))!;
}
