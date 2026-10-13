using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using static KanbanBoard.Tests.EpicPlanImportApiTests;
using static KanbanBoard.Tests.ProgramDependencyWiringTests;

namespace KanbanBoard.Tests;

// The idempotency key of the creates of an epic, an epic document and a work item, through the real application
// (Program.cs) on SQLite on a file.
public sealed class CreateIdempotencyApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // Each create with the body of a first call and of a repeat with other content; {0} is the id of its parent.
    public static TheoryData<string> Kinds => new() { "epic", "document", "item" };

    private sealed record Created(Guid Id, bool? WasCreated, string Name, HttpStatusCode Status);

    private sealed class Scenario(ApiApplication api, string kind, Guid project, Guid epic)
    {
        public Guid Project => project;

        public Guid Epic => epic;

        public async Task<Created> CreateAsync(HttpClient client, string? key, string name = "First")
        {
            using var response = kind switch
            {
                "epic" => await client.PostAsJsonAsync($"/api/projects/{project}/epics", new { name, idempotencyKey = key }),
                "document" => await client.PostAsJsonAsync($"/api/epics/{epic}/documents", new { title = name, body = "body", idempotencyKey = key }),
                _ => await client.PostAsJsonAsync("/api/items", new { projectId = project, title = name, type = "Task", status = "Ready", priority = "Medium", idempotencyKey = key })
            };
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            return new(
                root.GetProperty("id").GetGuid(),
                root.TryGetProperty("created", out var created) ? created.GetBoolean() : null,
                root.GetProperty(kind == "epic" ? "name" : "title").GetString()!,
                response.StatusCode);
        }

        public async Task<(int Rows, string? Hash)> StoredAsync(Guid id)
        {
            using var dbContext = api.OpenDatabase();
            return kind switch
            {
                "epic" => (await dbContext.Epics.CountAsync(), (await dbContext.Epics.SingleAsync(row => row.Id == id)).IdempotencyKeyHash),
                "document" => (await dbContext.EpicDocuments.CountAsync(), (await dbContext.EpicDocuments.SingleAsync(row => row.Id == id)).IdempotencyKeyHash),
                _ => (await dbContext.WorkItems.CountAsync(), (await dbContext.WorkItems.SingleAsync(row => row.Id == id)).IdempotencyKeyHash)
            };
        }
    }

    // A project with one epic (created without a key), which is the parent of the documents.
    private static async Task<Scenario> StartAsync(ApiApplication api, HttpClient client, string kind, string projectKey = "KEYED")
    {
        var project = await CreateProjectAsync(client, projectKey);
        using var response = await client.PostAsJsonAsync($"/api/projects/{project}/epics", new { name = "Parent" });
        response.EnsureSuccessStatusCode();
        return new(api, kind, project, (await response.Content.ReadFromJsonAsync<EpicDto>(Json))!.Id);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_keyed_create_stores_the_hash_of_the_key_and_says_it_created(string kind)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, kind);

        var created = await scenario.CreateAsync(client, "run-1");

        Assert.Equal((true, "First", HttpStatusCode.Created), (created.WasCreated, created.Name, created.Status));
        using var dbContext = api.OpenDatabase();
        var user = await dbContext.AppUsers.SingleAsync(candidate => candidate.DisplayName == "Factory");
        // The key itself is not stored.
        Assert.Equal(IdempotencyKeys.Hash(user.Id, "run-1"), (await scenario.StoredAsync(created.Id)).Hash);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_repeat_of_the_key_returns_the_same_object_as_it_is_now_and_creates_nothing(string kind)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, kind);
        var first = await scenario.CreateAsync(client, "run-1");
        var before = await CountsAsync(api);

        var repeat = await scenario.CreateAsync(client, "run-1");
        // The key names the object: a repeat with other content still reports the first one, unchanged.
        var other = await scenario.CreateAsync(client, "run-1", "Other");

        Assert.All(new[] { repeat, other }, answer =>
            Assert.Equal((first.Id, false, "First", HttpStatusCode.OK), (answer.Id, answer.WasCreated, answer.Name, answer.Status)));
        // No row and no entry of the change feed was added.
        Assert.Equal(before, await CountsAsync(api));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_key_belongs_to_one_identity_and_one_parent_and_is_compared_exactly(string kind)
    {
        using var api = new ApiApplication();
        using var factory = api.CreateCallerClient("Factory");
        using var other = api.CreateCallerClient("Other");
        using var anonymous = api.CreateClient();
        var scenario = await StartAsync(api, factory, kind);
        var elsewhere = await StartAsync(api, factory, kind, "ELSEWHERE");

        var ids = new[]
        {
            (await scenario.CreateAsync(factory, "key")).Id,
            (await scenario.CreateAsync(other, "key")).Id,
            (await scenario.CreateAsync(factory, "KEY")).Id,
            (await elsewhere.CreateAsync(factory, "key")).Id,
            (await scenario.CreateAsync(anonymous, "key")).Id,
            // Without a key every call creates.
            (await scenario.CreateAsync(factory, null)).Id,
            (await scenario.CreateAsync(factory, null)).Id
        };
        // Callers without a token share one identity.
        var anonymousRepeat = await scenario.CreateAsync(anonymous, "key");

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal((ids[4], false), (anonymousRepeat.Id, anonymousRepeat.WasCreated));
        Assert.Null((await scenario.StoredAsync(ids[5])).Hash);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Creates_with_one_key_at_the_same_moment_create_once_and_all_report_the_same_id(string kind)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, kind);
        var rowsBefore = kind == "epic" ? 1 : 0;

        // Half of them race creates without a key, which must all go through.
        var keyed = Enumerable.Range(0, 8).Select(_ => Task.Run(() => scenario.CreateAsync(client, "run-1"))).ToList();
        var plain = Enumerable.Range(0, 4).Select(_ => Task.Run(() => scenario.CreateAsync(client, null, "Plain"))).ToList();
        var answers = await Task.WhenAll(keyed);
        await Task.WhenAll(plain);

        Assert.Single(answers.Select(answer => answer.Id).Distinct());
        Assert.Single(answers, answer => answer.WasCreated == true);
        Assert.Equal(rowsBefore + 5, (await scenario.StoredAsync(answers[0].Id)).Rows);
    }

    // A create with a key holds the row of its parent, which makes the creates in that parent wait for each other.
    // A create of an epic or a document without one must not: it is what the web UI and every caller from before
    // the key sends. A create of a work item holds its project either way, before it reads the end of its column:
    // two creates that read it at once would take the same Order.
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Only_a_keyed_create_holds_the_row_of_its_parent(string kind)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, kind);
        var parent = kind == "document" ? "Epics" : "Projects";

        api.Commands.Clear();
        await scenario.CreateAsync(client, null);
        var plain = api.Commands.ToList();
        api.Commands.Clear();
        await scenario.CreateAsync(client, "run-1");
        var keyed = api.Commands.ToList();

        Assert.DoesNotContain(plain, text => text.Contains("\"IdempotencyKeyHash\" =", StringComparison.Ordinal));
        if (kind == "item")
        {
            var heldByPlain = plain.FindIndex(text => text.StartsWith("UPDATE \"Projects\"", StringComparison.Ordinal));
            var columnEnd = plain.FindIndex(text => text.Contains("MAX(", StringComparison.Ordinal));
            Assert.True(heldByPlain >= 0 && columnEnd > heldByPlain, string.Join("\n---\n", plain));
            Assert.DoesNotContain(plain.Take(heldByPlain), text => text.Contains("FROM \"WorkItems\"", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(plain, text => text.StartsWith("UPDATE", StringComparison.Ordinal));
        }

        // The UPDATE is the first statement, before the look-up of the key.
        var held = keyed.FindIndex(text => text.StartsWith($"UPDATE \"{parent}\"", StringComparison.Ordinal));
        var lookedUp = keyed.FindIndex(text => text.Contains("\"IdempotencyKeyHash\" =", StringComparison.Ordinal));
        Assert.True(held >= 0 && lookedUp > held, string.Join("\n---\n", keyed));
        Assert.DoesNotContain(keyed.Take(held), text => text.Contains($"FROM \"{(kind == "epic" ? "Epics" : kind == "document" ? "EpicDocuments" : "WorkItems")}\"", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_malformed_key_is_refused_and_creates_nothing(string kind)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, kind);
        var before = await CountsAsync(api);

        foreach (var key in new[] { "", "two words", new string('k', IdempotencyKeyLimits.MaxLength + 1), "schlüssel" })
        {
            using var response = kind switch
            {
                "epic" => await client.PostAsJsonAsync($"/api/projects/{scenario.Project}/epics", new { name = "E", idempotencyKey = key }),
                "document" => await client.PostAsJsonAsync($"/api/epics/{scenario.Epic}/documents", new { title = "D", body = "b", idempotencyKey = key }),
                _ => await client.PostAsJsonAsync("/api/items", new { projectId = scenario.Project, title = "I", type = "Task", status = "Ready", priority = "Medium", idempotencyKey = key })
            };

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<KanbanErrorDto>(Json))!;
            Assert.Equal((KanbanErrorCodes.InvalidArgument, IdempotencyKeys.InvalidMessage), (error.Code, error.Message));
        }

        Assert.Equal(before, await CountsAsync(api));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_keyed_create_in_a_parent_that_does_not_exist_is_not_found(string kind)
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        await StartAsync(api, client, kind);
        var before = await CountsAsync(api);
        var missing = Guid.NewGuid();

        foreach (var key in new[] { "run-1", null })
        {
            using var response = kind switch
            {
                "epic" => await client.PostAsJsonAsync($"/api/projects/{missing}/epics", new { name = "E", idempotencyKey = key }),
                "document" => await client.PostAsJsonAsync($"/api/epics/{missing}/documents", new { title = "D", body = "b", idempotencyKey = key }),
                _ => await client.PostAsJsonAsync("/api/items", new { projectId = missing, title = "I", type = "Task", status = "Ready", priority = "Medium", idempotencyKey = key })
            };

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(KanbanErrorCodes.NotFound, (await response.Content.ReadFromJsonAsync<KanbanErrorDto>(Json))!.Code);
        }

        Assert.Equal(before, await CountsAsync(api));
    }

    [Fact]
    public async Task A_keyed_item_is_recorded_once_in_the_change_feed_and_a_repeat_returns_it_with_what_it_has_gained()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, "item");
        var body = new { projectId = scenario.Project, epicId = scenario.Epic, title = "Item", type = "Story", status = "Ready", priority = "High", estimate = 5, labelList = new[] { "api" }, idempotencyKey = "run-1" };

        var first = await PostAsync<WorkItemDto>(client, "/api/items", body, HttpStatusCode.Created);
        var blocker = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = scenario.Project, title = "Blocker", type = "Task", status = "Ready", priority = "Medium" }, HttpStatusCode.Created);
        (await client.PostAsJsonAsync($"/api/items/{first.Id}/dependencies", new AddWorkItemDependencyRequest(blocker.Id))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/items/{first.Id}/links", new AddWorkItemLinkRequest("pr", "https://github.com/acme/app/pull/1"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/items/{first.Id}/comments", new { body = "note" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/items/{first.Id}/move", new { status = "InProgress", order = 0 })).EnsureSuccessStatusCode();
        // The epic is archived: a new item could not be put in it, but the repeat is not a new item.
        (await client.PutAsJsonAsync($"/api/epics/{scenario.Epic}", new { name = "Parent", isArchived = true })).EnsureSuccessStatusCode();
        var before = await CountsAsync(api);

        var repeat = await PostAsync<WorkItemDto>(client, "/api/items", body, HttpStatusCode.OK);

        Assert.Equal((true, 1L, 0, "api", scenario.Epic), (first.Created, first.Version, first.Order, first.Labels, first.EpicId));
        Assert.Equal((first.Id, false, WorkItemStatus.InProgress, 3L), (repeat.Id, repeat.Created, repeat.Status, repeat.Version));
        Assert.Equal((blocker.Id, "pr", "note"), (Assert.Single(repeat.Blockers).Id, Assert.Single(repeat.Links).Kind, Assert.Single(repeat.Comments).Body));
        Assert.Null(repeat.EpicId);
        Assert.Equal(before, await CountsAsync(api));
        var feed = await client.GetFromJsonAsync<WorkItemChangePageDto>("/api/items/changes?fromStart=true&limit=200", Json);
        Assert.Single(feed!.Items, entry => entry.Kind == WorkItemChangeKinds.ItemCreated && entry.ItemId == first.Id);
        // "created" belongs to the answer of a create only.
        Assert.Null((await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{first.Id}", Json))!.Created);
    }

    [Fact]
    public async Task A_keyed_item_goes_to_the_end_of_its_column_and_into_an_epic_only_when_the_epic_takes_items()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, "item");
        var elsewhere = await StartAsync(api, client, "item", "ELSEWHERE");

        var first = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = scenario.Project, title = "1", type = "Task", status = "Ready", priority = "Medium", idempotencyKey = "one" }, HttpStatusCode.Created);
        var second = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = scenario.Project, title = "2", type = "Task", status = "Ready", priority = "Medium", idempotencyKey = "two" }, HttpStatusCode.Created);
        using var foreignEpic = await client.PostAsJsonAsync("/api/items", new { projectId = scenario.Project, epicId = elsewhere.Epic, title = "3", type = "Task", status = "Ready", priority = "Medium", idempotencyKey = "three" });

        Assert.Equal((0, 1), (first.Order, second.Order));
        Assert.Equal(HttpStatusCode.BadRequest, foreignEpic.StatusCode);
        var error = (await foreignEpic.Content.ReadFromJsonAsync<KanbanErrorDto>(Json))!;
        Assert.Equal((KanbanErrorCodes.InvalidArgument, WorkItemWrites.EpicRefusal), (error.Code, error.Message));
        // The refused create left its key free.
        var retried = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = scenario.Project, title = "3", type = "Task", status = "Ready", priority = "Medium", idempotencyKey = "three" }, HttpStatusCode.Created);
        Assert.Equal((true, 2), (retried.Created, retried.Order));
    }

    [Fact]
    public async Task The_keys_of_epics_and_items_of_one_project_are_separate()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, "item");

        var epic = await PostAsync<EpicDto>(client, $"/api/projects/{scenario.Project}/epics", new { name = "Epic", idempotencyKey = "same" }, HttpStatusCode.Created);
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = scenario.Project, title = "Item", type = "Task", status = "Ready", priority = "Medium", idempotencyKey = "same" }, HttpStatusCode.Created);
        using var import = await client.PostAsJsonAsync($"/api/projects/{scenario.Project}/epic-plans", new { idempotency_key = "same", epic = new { name = "Plan" }, items = new[] { new { @ref = "A", title = "a" } } });

        Assert.Equal((true, true, HttpStatusCode.Created), (epic.Created, item.Created, import.StatusCode));
    }

    [Fact]
    public async Task The_creates_answer_as_before_when_called_with_the_fields_they_always_had()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await CreateProjectAsync(client);

        var epic = await PostAsync<EpicDto>(client, $"/api/projects/{project}/epics", new { name = " Epic ", description = " d ", isArchived = false }, HttpStatusCode.Created);
        var document = await PostAsync<EpicDocumentDto>(client, $"/api/epics/{epic.Id}/documents", new { title = " Doc ", body = " body " }, HttpStatusCode.Created);
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project, epicId = epic.Id, title = " Item ", description = " d ", type = "Story", status = "Backlog", priority = "Medium", estimate = 2, labels = "a, b" }, HttpStatusCode.Created);

        Assert.Equal(("Epic", "d", true), (epic.Name, epic.Description, epic.Created));
        Assert.Equal(("Doc", " body ", true), (document.Title, document.Body, document.Created));
        Assert.Equal(("Item", "d", "a,b", epic.Id, "Epic", true, 1L), (item.Title, item.Description, item.Labels, item.EpicId, item.Epic!.Name, item.Created, item.Version));
        using var dbContext = api.OpenDatabase();
        Assert.Null((await dbContext.Epics.SingleAsync()).IdempotencyKeyHash);
        Assert.Null((await dbContext.EpicDocuments.SingleAsync()).IdempotencyKeyHash);
        Assert.Null((await dbContext.WorkItems.SingleAsync()).IdempotencyKeyHash);
        // "created" belongs to the answer of a create only.
        Assert.Null((await client.GetFromJsonAsync<EpicDto>($"/api/epics/{epic.Id}", Json))!.Created);
        Assert.Null((await client.GetFromJsonAsync<EpicDocumentDto>($"/api/epic-documents/{document.Id}", Json))!.Created);
    }

    [Fact]
    public async Task A_name_or_title_longer_than_its_column_is_refused_instead_of_failing_in_the_database()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await CreateProjectAsync(client);
        var epic = await PostAsync<EpicDto>(client, $"/api/projects/{project}/epics", new { name = new string('n', EpicLimits.MaxNameLength) }, HttpStatusCode.Created);

        using var longName = await client.PostAsJsonAsync($"/api/projects/{project}/epics", new { name = new string('n', EpicLimits.MaxNameLength + 1) });
        using var longTitle = await client.PostAsJsonAsync($"/api/epics/{epic.Id}/documents", new { title = new string('t', EpicLimits.MaxDocumentTitleLength + 1), body = "b" });

        foreach (var response in new[] { longName, longTitle })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(KanbanErrorCodes.InvalidArgument, (await response.Content.ReadFromJsonAsync<KanbanErrorDto>(Json))!.Code);
        }
    }

    // Found on MariaDB with imports racing delete_project: the update answered 500.
    [Fact]
    public async Task An_update_of_an_epic_that_is_deleted_while_the_update_runs_is_not_found()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var scenario = await StartAsync(api, client, "item");
        api.BeforeNextTransaction = () =>
        {
            using var dbContext = api.OpenDatabase();
            dbContext.Epics.Where(epic => epic.Id == scenario.Epic).ExecuteDelete();
        };

        using var response = await client.PutAsJsonAsync($"/api/epics/{scenario.Epic}", new { name = "Renamed", isArchived = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<T>(raw, Json)!;
    }
}
