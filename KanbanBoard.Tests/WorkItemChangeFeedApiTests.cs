using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static KanbanBoard.Tests.ProgramDependencyWiringTests;
using static KanbanBoard.Tests.WorkItemClaimApiTests;

namespace KanbanBoard.Tests;

// The change feed through the real application (Program.cs) on SQLite on a file: every write path appends its
// entry in the transaction of the write, a refused write appends nothing, and the feed is read from a cursor.
public sealed class WorkItemChangeFeedApiTests
{
    private const string Url = "https://github.com/acme/app/pull/42";

    [Fact]
    public async Task Every_kind_of_write_appends_one_entry_with_the_item_the_kind_the_actor_the_old_and_new_values_and_the_time()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        var actor = await ActorAsync(api, "Factory");
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "FEED" });
        var epic = await PostAsync<EpicDto>(client, $"/api/projects/{project.Id}/epics", new { name = "Epic" });
        var head = await HeadAsync(client);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var blocker = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Blocker", type = "Task", status = "Backlog", priority = "Medium" });
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, epicId = epic.Id, title = "Item", description = "first", type = "Story", status = "Backlog", priority = "High", estimate = 3, labels = "api,ui" });
        var claimed = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/claim", new ClaimWorkItemRequest("run-1", 60));
        var renewed = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/claim/renew", new ClaimWorkItemRequest("run-1", 600));
        await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/move", new { status = "InProgress", order = 0, expectedVersion = 1 });
        await SendAsync<WorkItemDto>(client, HttpMethod.Patch, $"/api/items/{item.Id}", new { title = "Renamed", estimate = (int?)null, labels = new[] { "api" }, expectedVersion = 2 });
        await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{item.Id}", new { status = "Done" });
        var commented = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/comments", new { author = "ignored", body = "shipped" });
        var linked = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/links", new AddWorkItemLinkRequest("pr", Url));
        await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/dependencies", new AddWorkItemDependencyRequest(blocker.Id));
        (await client.DeleteAsync($"/api/items/{item.Id}/dependencies/{blocker.Id}")).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/items/{item.Id}/links?kind=pr&url={Uri.EscapeDataString(Url)}")).EnsureSuccessStatusCode();
        await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/claim/release", new ReleaseWorkItemClaimRequest("run-1"));
        (await client.DeleteAsync($"/api/epics/{epic.Id}")).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/items/{item.Id}")).EnsureSuccessStatusCode();

        var entries = (await ReadAsync(client, head)).Items;
        var linkId = Assert.Single(linked.Links).Id;
        var commentId = commented.CommentId;
        string?[] expiries = [Iso(claimed.ClaimExpiresAtUtc), Iso(renewed.ClaimExpiresAtUtc)];
        Assert.Equal(
            [
                ("item_created", blocker.Id, "title: null > Blocker; type: null > Task; status: null > Backlog; priority: null > Medium; order: null > 0; version: null > 1"),
                ("item_created", item.Id, $"title: null > Item; description: null > first; type: null > Story; status: null > Backlog; priority: null > High; order: null > 1; estimate: null > 3; labels: null > api,ui; epic_id: null > {epic.Id}; version: null > 1"),
                ("claimed", item.Id, $"claimant_id: null > {actor.Id}; claimant: null > Factory; claim_marker: null > run-1; claim_expires_at_utc: null > {expiries[0]}"),
                ("claim_renewed", item.Id, $"claim_expires_at_utc: {expiries[0]} > {expiries[1]}"),
                ("item_moved", item.Id, "status: Backlog > InProgress; order: 1 > 0; version: 1 > 2"),
                ("item_updated", item.Id, "title: Item > Renamed; estimate: 3 > null; labels: api,ui > api; version: 2 > 3"),
                ("item_updated", item.Id, "status: InProgress > Done; version: 3 > 4"),
                ("comment_added", item.Id, $$"""comment: null > {"id":"{{commentId}}","author":"Factory","body":"shipped"}"""),
                ("link_added", item.Id, $$"""link: null > {"id":"{{linkId}}","kind":"pr","url":"{{Url}}"}"""),
                ("dependency_added", item.Id, $"blocked_by: null > {blocker.Id}"),
                ("dependency_removed", item.Id, $"blocked_by: {blocker.Id} > null"),
                ("link_removed", item.Id, $$"""link: {"id":"{{linkId}}","kind":"pr","url":"{{Url}}"} > null"""),
                ("claim_released", item.Id, $"claimant_id: {actor.Id} > null; claimant: Factory > null; claim_marker: run-1 > null; claim_expires_at_utc: {expiries[1]} > null"),
                ("item_updated", item.Id, $"epic_id: {epic.Id} > null; version: 4 > 5"),
                ("item_deleted", item.Id, "title: Renamed > null; description: first > null; type: Story > null; status: Done > null; priority: High > null; order: 0 > null; labels: api > null; version: 5 > null")
            ],
            entries.Select(entry => (entry.Kind, entry.ItemId, Describe(entry))).ToArray());
        Assert.All(entries, entry =>
        {
            Assert.Equal(project.Id, entry.ProjectId);
            Assert.Equal(actor, entry.Actor);
            Assert.InRange(entry.CreatedAtUtc, before, DateTimeOffset.UtcNow.AddSeconds(1));
        });
        Assert.Equal(entries.Select(entry => entry.Sequence).Order(), entries.Select(entry => entry.Sequence));
        Assert.Equal(entries.Count, entries.Select(entry => entry.Sequence).Distinct().Count());
        Assert.All(entries, entry => Assert.Contains(entry.Kind, WorkItemChangeKinds.All));
    }

    [Fact]
    public async Task A_write_that_is_refused_or_changes_nothing_appends_no_entry()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        using var other = api.CreateCallerClient("Other");
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "REFUSE" });
        var a = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "B", type = "Task", status = "Backlog", priority = "Medium" });
        await PostAsync<WorkItemDto>(client, $"/api/items/{a.Id}/claim", new ClaimWorkItemRequest("run-1", 60));
        await PostAsync<WorkItemDto>(client, $"/api/items/{a.Id}/links", new AddWorkItemLinkRequest("pr", Url));
        await PostAsync<WorkItemDto>(client, $"/api/items/{a.Id}/dependencies", new AddWorkItemDependencyRequest(b.Id));
        await PostAsync<WorkItemDto>(client, $"/api/items/{a.Id}/comments", new { body = "note", idempotencyKey = "key" });
        var head = await HeadAsync(client);
        var unknown = Guid.NewGuid();

        (HttpStatusCode Status, string? Code)[] answers =
        [
            // conflict: a stale expected version, on an update and on a move; a renew and a release of a claim not held.
            await AnswerAsync(client.PatchAsJsonAsync($"/api/items/{a.Id}", new { title = "Renamed", expectedVersion = 9 })),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{a.Id}/move", new { status = "Done", order = 0, expectedVersion = 9 })),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{b.Id}/claim/renew", new ClaimWorkItemRequest("run-1", 60))),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{b.Id}/claim/release", new ReleaseWorkItemClaimRequest("run-1"))),
            // already_claimed: another marker, and another identity, on a live claim.
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{a.Id}/claim", new ClaimWorkItemRequest("run-2", 60))),
            await AnswerAsync(other.PostAsJsonAsync($"/api/items/{a.Id}/claim", new ClaimWorkItemRequest("run-1", 60))),
            await AnswerAsync(other.PostAsJsonAsync($"/api/items/{a.Id}/claim/release", new ReleaseWorkItemClaimRequest("run-1"))),
            // cycle.
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{b.Id}/dependencies", new AddWorkItemDependencyRequest(a.Id))),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{a.Id}/dependencies", new AddWorkItemDependencyRequest(a.Id))),
            // invalid_argument and not_found.
            await AnswerAsync(client.PatchAsJsonAsync($"/api/items/{a.Id}", new { title = " " })),
            await AnswerAsync(client.PatchAsJsonAsync($"/api/items/{a.Id}", new { epicId = unknown })),
            await AnswerAsync(client.PatchAsJsonAsync($"/api/items/{unknown}", new { title = "x" })),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{unknown}/move", new { status = "Done", order = 0 })),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{unknown}/claim", new ClaimWorkItemRequest("run-1", 60))),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{unknown}/comments", new { body = "note" })),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{unknown}/links", new AddWorkItemLinkRequest("pr", Url))),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{a.Id}/dependencies", new AddWorkItemDependencyRequest(unknown))),
            await AnswerAsync(client.DeleteAsync($"/api/items/{unknown}")),
            // Writes that succeed and change nothing: what they ask for is already so.
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{a.Id}/links", new AddWorkItemLinkRequest("pr", Url))),
            await AnswerAsync(client.DeleteAsync($"/api/items/{a.Id}/links?linkId={unknown}")),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{a.Id}/dependencies", new AddWorkItemDependencyRequest(b.Id))),
            await AnswerAsync(client.DeleteAsync($"/api/items/{b.Id}/dependencies/{a.Id}")),
            await AnswerAsync(client.PostAsJsonAsync($"/api/items/{a.Id}/comments", new { body = "another", idempotencyKey = "key" }))
        ];

        var page = await ReadAsync(client, head);
        Assert.Equal(
            [
                (HttpStatusCode.Conflict, KanbanErrorCodes.Conflict), (HttpStatusCode.Conflict, KanbanErrorCodes.Conflict),
                (HttpStatusCode.Conflict, KanbanErrorCodes.Conflict), (HttpStatusCode.Conflict, KanbanErrorCodes.Conflict),
                (HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed), (HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed),
                (HttpStatusCode.Conflict, KanbanErrorCodes.AlreadyClaimed),
                (HttpStatusCode.Conflict, KanbanErrorCodes.Cycle), (HttpStatusCode.Conflict, KanbanErrorCodes.Cycle),
                (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument), (HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound), (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound), (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound), (HttpStatusCode.NotFound, KanbanErrorCodes.NotFound),
                (HttpStatusCode.NotFound, null),
                (HttpStatusCode.OK, null), (HttpStatusCode.OK, null), (HttpStatusCode.OK, null), (HttpStatusCode.OK, null), (HttpStatusCode.OK, null)
            ],
            answers);
        Assert.Empty(page.Items);
        // A reader that found nothing keeps its place.
        Assert.Equal(head, page.NextCursor);
    }

    [Fact]
    public async Task The_entry_is_stored_with_the_write_or_not_at_all()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "ATOMIC" });
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Title", type = "Task", status = "Backlog", priority = "Medium" });
        var head = await HeadAsync(client);

        (string Path, HttpMethod Method, object Body)[] writes =
        [
            ($"/api/items/{item.Id}", HttpMethod.Patch, new { title = "Renamed" }),
            ($"/api/items/{item.Id}/move", HttpMethod.Post, new { status = "Done", order = 0 }),
            ($"/api/items/{item.Id}/comments", HttpMethod.Post, new { author = "tester", body = "note" }),
            ($"/api/items/{item.Id}/links", HttpMethod.Post, new AddWorkItemLinkRequest("pr", Url)),
            ($"/api/items", HttpMethod.Post, new { projectId = project.Id, title = "Another", type = "Task", status = "Backlog", priority = "Medium" })
        ];
        foreach (var (path, method, body) in writes)
        {
            // The save of the entry fails: the write it belongs to must not be stored either.
            api.FailNextChangeLogSave = true;
            using var response = await client.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) });
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.False(api.FailNextChangeLogSave);
        }

        var after = await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{item.Id}", Json);
        var all = await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items?projectId={project.Id}", Json);
        Assert.Equal(
            (item.Title, item.Status, item.Version, item.UpdatedAtUtc, 0, 0),
            (after!.Title, after.Status, after.Version, after.UpdatedAtUtc, after.Comments.Count, after.Links.Count));
        Assert.Equal(item.Id, Assert.Single(all!).Id);
        Assert.Empty((await ReadAsync(client, head)).Items);
    }

    [Fact]
    public async Task Reading_from_a_cursor_returns_the_entries_after_it_in_write_order_and_the_same_ones_again_also_after_a_restart()
    {
        var first = new ApiApplication { KeepDatabase = true };
        string head, afterTwo;
        List<WorkItemChangeDto> entries;
        Guid project;
        using (first)
        using (var client = first.CreateClient())
        {
            project = (await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "ORDER" })).Id;
            await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project, title = "Before", type = "Task", status = "Backlog", priority = "Medium" });

            // The cursor to follow from is taken before the writes: it is the position of the newest entry.
            head = await HeadAsync(client);
            var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project, title = "Item", type = "Task", status = "Backlog", priority = "Medium" });
            for (var index = 0; index < 4; index++)
            {
                await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/comments", new { author = "tester", body = $"note {index}" });
            }

            var page = await ReadAsync(client, head);
            var again = await ReadAsync(client, head);
            var firstTwo = await ReadAsync(client, head, limit: 2);
            var rest = await ReadAsync(client, firstTwo.NextCursor, limit: 2);
            var end = await ReadAsync(client, rest.NextCursor, limit: 2);
            var atEnd = await ReadAsync(client, end.NextCursor);
            var fromStart = await client.GetFromJsonAsync<WorkItemChangePageDto>("/api/items/changes?fromStart=true&limit=1", Json);

            entries = page.Items.ToList();
            afterTwo = firstTwo.NextCursor;
            Assert.Equal(
                ["item_created", "comment_added", "comment_added", "comment_added", "comment_added"],
                entries.Select(entry => entry.Kind).ToArray());
            Assert.Equal(["note 0", "note 1", "note 2", "note 3"], entries.Skip(1).Select(entry => entry.Changes[0].GetProperty("new").GetProperty("body").GetString()!).ToArray());
            Assert.Equal(Raw(page), Raw(again));
            // Pages follow each other without a gap or a repeat, and the last cursor is the place to wait at.
            Assert.Equal(Sequences(entries), Sequences(firstTwo.Items.Concat(rest.Items).Concat(end.Items)));
            Assert.Equal((2, 2, 1, 0), (firstTwo.Items.Count, rest.Items.Count, end.Items.Count, atEnd.Items.Count));
            Assert.Equal((page.NextCursor, page.NextCursor), (end.NextCursor, atEnd.NextCursor));
            // The oldest entry of the feed is the item made before the cursor was taken.
            Assert.Equal("item_created", Assert.Single(fromStart!.Items).Kind);
            Assert.True(fromStart.Items[0].Sequence < entries[0].Sequence);
        }

        using var restarted = new ApiApplication(first);
        using var restartedClient = restarted.CreateClient();
        var replay = await ReadAsync(restartedClient, head);
        var resumed = await ReadAsync(restartedClient, afterTwo);
        await PostAsync<WorkItemDto>(restartedClient, "/api/items", new { projectId = project, title = "Later", type = "Task", status = "Backlog", priority = "Medium" });
        var followed = await ReadAsync(restartedClient, replay.NextCursor);

        Assert.Equal(JsonSerializer.Serialize(entries), JsonSerializer.Serialize(replay.Items));
        Assert.Equal(Sequences(entries.Skip(2)), Sequences(resumed.Items));
        // The feed goes on after the last sequence of before the restart.
        Assert.True(Assert.Single(followed.Items).Sequence > entries[^1].Sequence);
    }

    [Fact]
    public async Task Project_id_restricts_the_feed_to_that_project_and_one_cursor_serves_every_filter()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var one = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "One", key = "ONE" });
        var two = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Two", key = "TWO" });
        var head = await HeadAsync(client);
        var created = new List<(Guid Project, Guid Item)>();
        foreach (var project in new[] { one.Id, two.Id, one.Id, two.Id, two.Id })
        {
            created.Add((project, (await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project, title = "Item", type = "Task", status = "Backlog", priority = "Medium" })).Id));
        }

        // A dependency across projects is one entry, on the blocked item and so in its project.
        await PostAsync<WorkItemDto>(client, $"/api/items/{created[0].Item}/dependencies", new AddWorkItemDependencyRequest(created[1].Item));

        var all = await ReadAsync(client, head);
        var ofOne = await ReadAsync(client, head, one.Id);
        var ofTwo = await ReadAsync(client, head, two.Id, limit: 2);
        var restOfTwo = await ReadAsync(client, ofTwo.NextCursor, two.Id);
        var ofNone = await ReadAsync(client, head, Guid.NewGuid());
        // The cursor a filtered read returned is a position in the whole feed.
        var afterOne = await ReadAsync(client, ofOne.NextCursor);

        Assert.Equal(6, all.Items.Count);
        Assert.Equal(
            [(created[0].Item, "item_created"), (created[2].Item, "item_created"), (created[0].Item, "dependency_added")],
            ofOne.Items.Select(entry => (entry.ItemId, entry.Kind)).ToArray());
        Assert.All(ofOne.Items, entry => Assert.Equal(one.Id, entry.ProjectId));
        Assert.Equal([created[1].Item, created[3].Item], ofTwo.Items.Select(entry => entry.ItemId).ToArray());
        Assert.Equal(created[4].Item, Assert.Single(restOfTwo.Items).ItemId);
        Assert.Empty(ofNone.Items);
        Assert.Equal(head, ofNone.NextCursor);
        Assert.Empty(afterOne.Items);
    }

    [Fact]
    public async Task Without_a_cursor_the_answer_is_the_place_to_start_from_and_no_entry()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "HEAD" });

        var empty = await client.GetFromJsonAsync<WorkItemChangePageDto>("/api/items/changes", Json);
        var emptyFromStart = await client.GetFromJsonAsync<WorkItemChangePageDto>("/api/items/changes?fromStart=true", Json);
        await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Item", type = "Task", status = "Backlog", priority = "Medium" });
        var head = await client.GetFromJsonAsync<WorkItemChangePageDto>($"/api/items/changes?projectId={Guid.NewGuid()}&limit=5", Json);
        var afterEmpty = await ReadAsync(client, empty!.NextCursor);
        var afterEmptyStart = await ReadAsync(client, emptyFromStart!.NextCursor);

        // An empty feed has a place to start from as well, and what is written later comes after it.
        Assert.Empty(empty.Items);
        Assert.Empty(emptyFromStart.Items);
        Assert.Single(afterEmpty.Items);
        Assert.Single(afterEmptyStart.Items);
        Assert.Empty(head!.Items);
        Assert.Equal(afterEmpty.NextCursor, head.NextCursor);
        Assert.Empty((await ReadAsync(client, head.NextCursor)).Items);
    }

    [Fact]
    public async Task A_cursor_that_is_not_one_of_the_feed_and_a_limit_out_of_range_are_refused_invalid_argument()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var head = await HeadAsync(client);
        string[] queries =
        [
            "cursor=not-a-cursor",
            "cursor=",
            // The cursors of the other lists are not positions in the feed.
            $"cursor={new WorkItemCursor(DateTimeOffset.UtcNow, Guid.NewGuid()).Encode(WorkItemCursor.WorkItems)}",
            $"cursor={new WorkItemCursor(DateTimeOffset.UtcNow, Guid.NewGuid()).Encode(WorkItemCursor.Comments)}",
            $"cursor={head}&fromStart=true",
            $"cursor={head}&limit=0",
            $"cursor={head}&limit=201",
            "limit=0"
        ];

        foreach (var query in queries)
        {
            using var response = await client.GetAsync($"/api/items/changes?{query}");
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        }

        Assert.True(WorkItemChangeLog.TryDecodeCursor(WorkItemChangeLog.EncodeCursor(long.MaxValue), out var sequence));
        Assert.Equal(long.MaxValue, sequence);
        Assert.False(WorkItemChangeLog.TryDecodeCursor(WorkItemChangeLog.EncodeCursor(-1), out _));
    }

    [Fact]
    public async Task Entries_stay_readable_after_their_item_and_their_project_are_deleted()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "GONE" });
        var head = await HeadAsync(client);
        var a = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "B", type = "Task", status = "Ready", priority = "Medium" });
        await PostAsync<WorkItemDto>(client, $"/api/items/{a.Id}/comments", new { author = "tester", body = "note" });

        (await client.DeleteAsync($"/api/items/{a.Id}")).EnsureSuccessStatusCode();
        // Deleting the project deletes the item that is left, which is a change to it.
        (await client.DeleteAsync($"/api/projects/{project.Id}")).EnsureSuccessStatusCode();

        var entries = (await ReadAsync(client, head, project.Id)).Items;
        Assert.Equal(
            [("item_created", a.Id), ("item_created", b.Id), ("comment_added", a.Id), ("item_deleted", a.Id), ("item_deleted", b.Id)],
            entries.Select(entry => (entry.Kind, entry.ItemId)).ToArray());
        Assert.Equal("title: B > null; type: Task > null; status: Ready > null; priority: Medium > null; order: 0 > null; version: 1 > null", Describe(entries[4]));
        using var dbContext = api.OpenDatabase();
        Assert.Empty(await dbContext.WorkItems.ToListAsync());
    }

    [Fact]
    public async Task Archiving_an_epic_logs_the_unfinished_items_it_takes_out_and_deleting_it_logs_the_rest()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "EPIC" });
        var epic = await PostAsync<EpicDto>(client, $"/api/projects/{project.Id}/epics", new { name = "Epic" });
        var open = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, epicId = epic.Id, title = "Open", type = "Task", status = "Backlog", priority = "Medium" });
        var done = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, epicId = epic.Id, title = "Done", type = "Task", status = "Done", priority = "Medium" });
        var outside = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Outside", type = "Task", status = "Backlog", priority = "Medium" });
        var head = await HeadAsync(client);

        await SendAsync<EpicDto>(client, HttpMethod.Put, $"/api/epics/{epic.Id}", new { name = "Epic", isArchived = true });
        var archived = await ReadAsync(client, head);
        // A rename of the epic changes no item.
        await SendAsync<EpicDto>(client, HttpMethod.Put, $"/api/epics/{epic.Id}", new { name = "Renamed", isArchived = true });
        (await client.DeleteAsync($"/api/epics/{epic.Id}")).EnsureSuccessStatusCode();
        var deleted = await ReadAsync(client, archived.NextCursor);

        var first = Assert.Single(archived.Items);
        var second = Assert.Single(deleted.Items);
        Assert.Equal(("item_updated", open.Id, $"epic_id: {epic.Id} > null; version: 1 > 2"), (first.Kind, first.ItemId, Describe(first)));
        Assert.Equal(("item_updated", done.Id, $"epic_id: {epic.Id} > null; version: 1 > 2"), (second.Kind, second.ItemId, Describe(second)));
        Assert.Equal(1L, (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{outside.Id}", Json))!.Version);
    }

    [Fact]
    public async Task A_move_logs_the_moved_item_only_whatever_siblings_it_renumbers()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "SIBS" });
        var items = new List<WorkItemDto>();
        foreach (var title in new[] { "A", "B", "C" })
        {
            items.Add(await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title, type = "Task", status = "Backlog", priority = "Medium" }));
        }

        var head = await HeadAsync(client);

        await PostAsync<WorkItemDto>(client, $"/api/items/{items[2].Id}/move", new { status = "Backlog", order = 0 });

        var entry = Assert.Single((await ReadAsync(client, head)).Items);
        Assert.Equal(("item_moved", items[2].Id, "order: 2 > 0; version: 1 > 2"), (entry.Kind, entry.ItemId, Describe(entry)));
        // The siblings did move down.
        Assert.Equal(1, (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{items[0].Id}", Json))!.Order);
    }

    [Fact]
    public async Task A_claim_that_takes_over_or_repeats_a_claim_reports_the_claim_that_was_stored()
    {
        using var api = new ApiApplication();
        using var client = api.CreateCallerClient("Factory");
        using var other = api.CreateCallerClient("Other");
        var factory = await ActorAsync(api, "Factory");
        var otherActor = await ActorAsync(api, "Other");
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "CLAIM" });
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Item", type = "Task", status = "Backlog", priority = "Medium" });
        var head = await HeadAsync(client);

        var first = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/claim", new ClaimWorkItemRequest("run-1", 1));
        // The same marker again resets the expiry; nothing else of the claim changes.
        var repeated = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/claim", new ClaimWorkItemRequest("run-1", 1));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        // The claim has lapsed and was never cleared: the takeover replaces what is still stored.
        var takenOver = await PostAsync<WorkItemDto>(other, $"/api/items/{item.Id}/claim", new ClaimWorkItemRequest("run-2", 60));

        var entries = (await ReadAsync(client, head)).Items;
        Assert.Equal(["claimed", "claimed", "claimed"], entries.Select(entry => entry.Kind).ToArray());
        Assert.Equal([factory, factory, otherActor], entries.Select(entry => entry.Actor!).ToArray());
        Assert.Equal($"claim_expires_at_utc: {Iso(first.ClaimExpiresAtUtc)} > {Iso(repeated.ClaimExpiresAtUtc)}", Describe(entries[1]));
        Assert.Equal(
            $"claimant_id: {factory.Id} > {otherActor.Id}; claimant: Factory > Other; claim_marker: run-1 > run-2; claim_expires_at_utc: {Iso(repeated.ClaimExpiresAtUtc)} > {Iso(takenOver.ClaimExpiresAtUtc)}",
            Describe(entries[2]));
    }

    [Fact]
    public async Task A_write_without_a_token_or_a_session_has_no_actor_whatever_author_it_names()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "ANON" });
        var head = await HeadAsync(client);
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "Item", type = "Task", status = "Backlog", priority = "Medium" });

        await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/comments", new { author = "someone", body = "note" });

        var raw = await client.GetStringAsync($"/api/items/changes?cursor={head}");
        var entries = JsonSerializer.Deserialize<WorkItemChangePageDto>(raw, Json)!.Items;
        Assert.All(entries, entry => Assert.Null(entry.Actor));
        // The author a caller names is content of the comment, not the actor of the change.
        Assert.Equal("someone", entries[1].Changes[0].GetProperty("new").GetProperty("author").GetString());
        // The names of an entry, as the API writes them.
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(
            ["actor", "changes", "created_at_utc", "item_id", "kind", "project_id", "sequence"],
            json.RootElement.GetProperty("items")[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["items", "next_cursor"], json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task The_actor_is_the_owner_of_the_forwarded_token_or_the_user_of_the_browser_session()
    {
        await using var dbContext = TestDb.CreateContext();
        var owner = new AppUser { Issuer = "kanban:service-identity", Subject = "factory", DisplayName = "Factory" };
        var person = new AppUser { Issuer = "https://login.example", Subject = "sub-1", DisplayName = null, Email = "person@example.test" };
        var token = new PersonalAccessToken { AppUserId = owner.Id, Name = "token", TokenPrefix = "kbp", TokenHash = "hash", EncryptedSecret = "unused", RevokedAtUtc = DateTimeOffset.UtcNow };
        dbContext.AddRange(owner, person, token);
        await dbContext.SaveChangesAsync();
        var services = new ServiceCollection()
            .AddSingleton<ICurrentUserService>(new CurrentUserService(new HttpContextAccessor(), dbContext))
            .BuildServiceProvider();

        HttpContext Internal(string? tokenId)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Items[KanbanInternalAuth.InternalRequestItemKey] = true;
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "kanban-internal-service"), new(ClaimTypes.Name, "Kanban internal service") };
            if (tokenId is not null)
            {
                claims.Add(new Claim(KanbanInternalAuth.PersonalAccessTokenIdHeaderName, tokenId));
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, KanbanInternalAuth.AuthenticationType));
            return context;
        }

        HttpContext Session(string subject, string? name) => new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("sub", subject), new Claim("iss", "https://login.example") }.Concat(name is null ? [] : [new Claim("name", name)]),
                "oidc"))
        };

        // A token names its owner, also once it is revoked: whether it may write is decided elsewhere.
        Assert.Equal(new ChangeActor(owner.Id, "Factory"), await ChangeActor.ResolveAsync(Internal(token.Id.ToString()), dbContext, CancellationToken.None));
        // The MCP server without a token, and with one that does not exist, is nobody: never the name of the service.
        Assert.Null(await ChangeActor.ResolveAsync(Internal(null), dbContext, CancellationToken.None));
        Assert.Null(await ChangeActor.ResolveAsync(Internal(Guid.NewGuid().ToString()), dbContext, CancellationToken.None));
        // A browser session is its stored user, named as a claimant is: display name, else e-mail.
        Assert.Equal(new ChangeActor(person.Id, "person@example.test"), await ChangeActor.ResolveAsync(Session("sub-1", "Ignored"), dbContext, CancellationToken.None));
        // A session whose user was never stored still has the name it signed in with.
        Assert.Equal(new ChangeActor(null, "New Person"), await ChangeActor.ResolveAsync(Session("sub-2", "New Person"), dbContext, CancellationToken.None));
        Assert.Equal(new ChangeActor(null, "sub-3"), await ChangeActor.ResolveAsync(Session("sub-3", null), dbContext, CancellationToken.None));
        // No token and no session: authentication is off.
        Assert.Null(await ChangeActor.ResolveAsync(new DefaultHttpContext { RequestServices = services }, dbContext, CancellationToken.None));
    }

    [Fact]
    public async Task Writers_and_a_reader_that_polls_at_the_same_time_end_with_every_entry_once_and_in_order()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "RACE" });
        var items = new List<Guid>();
        for (var index = 0; index < 6; index++)
        {
            items.Add((await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = $"Item {index}", type = "Task", status = "Backlog", priority = "Medium" })).Id);
        }

        var cursor = await HeadAsync(client);
        var seen = new List<WorkItemChangeDto>();
        using var writing = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            // Small pages, as fast as they come: the reader is at the end of the feed most of the time.
            while (true)
            {
                var done = writing.IsCancellationRequested;
                var page = await ReadAsync(client, cursor, limit: 3);
                seen.AddRange(page.Items);
                cursor = page.NextCursor;
                if (done && page.Items.Count == 0)
                {
                    return;
                }
            }
        });

        var writes = await Task.WhenAll(items.SelectMany(item => Enumerable.Range(0, 8).Select(async index =>
        {
            using var response = index % 2 == 0
                ? await client.PostAsJsonAsync($"/api/items/{item}/comments", new { author = "tester", body = $"note {index}" })
                : await client.PatchAsJsonAsync($"/api/items/{item}", new { description = $"description {index}" });
            return response.IsSuccessStatusCode;
        })));
        writing.Cancel();
        await reader;

        using var dbContext = api.OpenDatabase();
        var stored = await dbContext.WorkItemChanges.OrderBy(change => change.Sequence).Select(change => change.Sequence).ToListAsync();
        Assert.All(writes, Assert.True);
        // Every entry that was committed, each once, in the order of the feed. The six creates came before the cursor.
        Assert.Equal(stored.Skip(items.Count), seen.Select(entry => entry.Sequence));
        Assert.Equal(writes.Length, seen.Count);
    }

    private static async Task<string> HeadAsync(HttpClient client)
    {
        var head = await client.GetFromJsonAsync<WorkItemChangePageDto>("/api/items/changes", Json);
        Assert.Empty(head!.Items);
        return head.NextCursor;
    }

    private static async Task<WorkItemChangePageDto> ReadAsync(HttpClient client, string cursor, Guid? projectId = null, int limit = 200)
    {
        using var response = await client.GetAsync(
            $"/api/items/changes?cursor={Uri.EscapeDataString(cursor)}&limit={limit}{(projectId is null ? string.Empty : $"&projectId={projectId}")}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<WorkItemChangePageDto>(body, Json)!;
    }

    private static async Task<(HttpStatusCode, string?)> AnswerAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode || body.Length == 0 ? null : JsonSerializer.Deserialize<KanbanErrorDto>(body, Json)!.Code);
    }

    private static async Task<WorkItemChangeActorDto> ActorAsync(ApiApplication api, string name)
    {
        using var dbContext = api.OpenDatabase();
        var user = await dbContext.AppUsers.SingleAsync(candidate => candidate.DisplayName == name);
        return new WorkItemChangeActorDto(user.Id, name);
    }

    // "field: old > new; ..." with values as the JSON holds them.
    private static string Describe(WorkItemChangeDto entry) =>
        string.Join("; ", entry.Changes.EnumerateArray().Select(change =>
            $"{change.GetProperty("field").GetString()}: {Value(change.GetProperty("old"))} > {Value(change.GetProperty("new"))}"));

    private static string Value(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.String => value.GetString()!,
            _ => value.GetRawText()
        };

    private static string? Iso(DateTimeOffset? value) => value is null ? null : JsonSerializer.Serialize(value.Value).Trim('"');

    private static string Raw(WorkItemChangePageDto page) => JsonSerializer.Serialize(page);

    private static long[] Sequences(IEnumerable<WorkItemChangeDto> entries) => entries.Select(entry => entry.Sequence).ToArray();
}
