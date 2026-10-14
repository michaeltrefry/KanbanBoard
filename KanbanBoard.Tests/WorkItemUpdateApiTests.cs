using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using static KanbanBoard.Tests.ProgramDependencyWiringTests;

namespace KanbanBoard.Tests;

// The partial update, the expected version, labels as a list and estimate on every read, through the API's
// Program.cs started as the real application with SQLite in place of MariaDB.
public sealed class WorkItemUpdateApiTests
{
    [Fact]
    public async Task A_patch_with_one_field_changes_that_field_leaves_the_others_and_advances_the_version_by_one()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, epic) = await CreateProjectAsync(client, "ONE");
        var created = await CreateAsync(client, project, new { title = "Title", description = "Body", type = "Story", status = "Backlog", priority = "High", estimate = 5, labels = "api,backend", epicId = epic });
        await Task.Delay(20);

        var status = await PatchAsync(client, created.Id, new { status = "InProgress" });
        var title = await PatchAsync(client, created.Id, new { title = " Renamed " });
        var priority = await PatchAsync(client, created.Id, new { priority = "Low" });
        var type = await PatchAsync(client, created.Id, new { type = "Issue" });
        var description = await PatchAsync(client, created.Id, new { description = "Other" });
        var estimate = await PatchAsync(client, created.Id, new { estimate = 8 });
        var labels = await PatchAsync(client, created.Id, new { labels = new[] { "factory" } });
        var read = (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{created.Id}", Json))!;

        Assert.Equal(1, created.Version);
        Assert.Equal(
            ("Title", "Body", WorkItemType.Story, WorkItemStatus.InProgress, WorkItemPriority.High, 5, "api,backend", epic, 2L),
            (status.Title, status.Description, status.Type, status.Status, status.Priority, status.Estimate, status.Labels, status.EpicId, status.Version));
        Assert.True(status.UpdatedAtUtc > created.UpdatedAtUtc);
        Assert.Equal([3L, 4L, 5L, 6L, 7L, 8L], new[] { title, priority, type, description, estimate, labels }.Select(item => item.Version));
        Assert.Equal(
            ("Renamed", "Other", WorkItemType.Issue, WorkItemStatus.InProgress, WorkItemPriority.Low, 8, "factory", epic, 8L),
            (read.Title, read.Description, read.Type, read.Status, read.Priority, read.Estimate, read.Labels, read.EpicId, read.Version));
        Assert.Equal(epic, read.Epic!.Id);
    }

    [Fact]
    public async Task A_null_clears_each_optional_field_alone_and_a_required_field_cannot_be_cleared()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, epic) = await CreateProjectAsync(client, "CLR");
        var created = await CreateAsync(client, project, new { title = "Title", description = "Body", type = "Story", status = "Ready", priority = "High", estimate = 5, labels = "api", epicId = epic });

        var description = await PatchAsync(client, created.Id, new { description = (string?)null });
        var estimate = await PatchAsync(client, created.Id, new { estimate = (int?)null });
        var labels = await PatchAsync(client, created.Id, new { labels = (string?)null });
        var epicCleared = await PatchAsync(client, created.Id, new { epicId = (Guid?)null });

        Assert.Equal((null, 5, "api", epic), (description.Description, description.Estimate, description.Labels, description.EpicId));
        Assert.Equal((null, null, "api", epic), (estimate.Description, estimate.Estimate, estimate.Labels, estimate.EpicId));
        Assert.Equal((null, epic), (labels.Labels, labels.EpicId));
        Assert.Empty(labels.LabelList);
        Assert.Equal((null, null, "Title", WorkItemStatus.Ready, 5L), (epicCleared.EpicId, epicCleared.Epic, epicCleared.Title, epicCleared.Status, epicCleared.Version));
        foreach (var body in new object[] { new { title = (string?)null }, new { title = " " }, new { type = (string?)null }, new { status = (string?)null }, new { priority = (string?)null }, new { title = new string('t', 241) } })
        {
            using var refused = await client.PatchAsJsonAsync($"/api/items/{created.Id}", body);
            await AssertErrorAsync(refused, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        }

        Assert.Equal(epicCleared, await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{created.Id}", Json), Same);
    }

    [Fact]
    public async Task An_update_with_a_stale_expected_version_is_refused_conflict_and_writes_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "STALE");
        var created = await CreateAsync(client, project, new { title = "Title", type = "Task", status = "Backlog", priority = "Medium" });
        var current = await PatchAsync(client, created.Id, new { description = "by someone else" });
        api.Commands.Clear();

        using var stale = await client.PatchAsJsonAsync($"/api/items/{created.Id}", new { status = "Done", title = "Mine", expectedVersion = created.Version });
        var writes = Writes(api);
        var afterStale = await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{created.Id}", Json);
        var fresh = await PatchAsync(client, created.Id, new { status = "Done", expectedVersion = current.Version });

        var message = await AssertErrorAsync(stale, HttpStatusCode.Conflict, KanbanErrorCodes.Conflict);
        Assert.Contains("version 2, not 1", message, StringComparison.Ordinal);
        // The update names a status, so it first holds the row of the project, which changes nothing. The refusal
        // is the one conditional UPDATE matching no row; nothing else is written.
        Assert.Equal(2, writes.Count);
        Assert.StartsWith("UPDATE \"Projects\"", writes[0], StringComparison.Ordinal);
        Assert.Contains("SET \"IsArchived\" = \"p\".\"IsArchived\"", writes[0], StringComparison.Ordinal);
        var update = writes[1];
        Assert.StartsWith("UPDATE \"WorkItems\"", update, StringComparison.Ordinal);
        Assert.Contains("\"Version\" = \"w\".\"Version\" + 1", update, StringComparison.Ordinal);
        Assert.Contains("\"w\".\"Version\" = @expected", update, StringComparison.Ordinal);
        Assert.Equal(current, afterStale, Same);
        Assert.Equal((WorkItemStatus.Done, "Title", "by someone else", 3L), (fresh.Status, fresh.Title, fresh.Description, fresh.Version));
    }

    [Fact]
    public async Task An_update_takes_the_item_with_its_conditional_update_before_it_reads_or_writes_anything_else()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "FIRST");
        var created = await CreateAsync(client, project, new { title = "Title", type = "Task", status = "Backlog", priority = "Medium" });
        api.Commands.Clear();

        await PatchAsync(client, created.Id, new { title = "Renamed", expectedVersion = 1 });

        // On MariaDB the UPDATE holds the item's row: the updates of one item then run one at a time.
        Assert.StartsWith("UPDATE \"WorkItems\"", api.Commands[0], StringComparison.Ordinal);
        // The conditional UPDATE, the changed columns, and the entry of the change feed.
        var writes = Writes(api);
        Assert.Equal(3, writes.Count);
        Assert.StartsWith("INSERT INTO \"WorkItemChanges\"", writes[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_move_with_the_current_version_succeeds_and_one_with_a_stale_version_is_refused_and_moves_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "MOVE");
        var a = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await CreateAsync(client, project, new { title = "B", type = "Task", status = "Backlog", priority = "Medium" });
        var c = await CreateAsync(client, project, new { title = "C", type = "Task", status = "Backlog", priority = "Medium" });

        var moved = await PostAsync<WorkItemDto>(client, $"/api/items/{c.Id}/move", new { status = "Backlog", order = 0, expectedVersion = 1 });
        var afterMove = await ItemsAsync(client);
        api.Commands.Clear();
        using var stale = await client.PostAsJsonAsync($"/api/items/{c.Id}/move", new { status = "Done", order = 0, expectedVersion = 1 });
        var writes = Writes(api);
        var afterStale = await ItemsAsync(client);
        // Without an expected version a move is unconditional, as it always was.
        var unconditional = await PostAsync<WorkItemDto>(client, $"/api/items/{c.Id}/move", new { status = "Ready", order = 0 });

        Assert.Equal((WorkItemStatus.Backlog, 0, 2L), (moved.Status, moved.Order, moved.Version));
        Assert.Equal((1, 2, 0), (afterMove[a.Id].Order, afterMove[b.Id].Order, afterMove[c.Id].Order));
        // The siblings were renumbered, which is not a change of theirs: version and updatedAtUtc stay.
        Assert.Equal((1L, a.UpdatedAtUtc, 1L, b.UpdatedAtUtc), (afterMove[a.Id].Version, afterMove[a.Id].UpdatedAtUtc, afterMove[b.Id].Version, afterMove[b.Id].UpdatedAtUtc));
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, KanbanErrorCodes.Conflict);
        // The statements that hold the project and the item, which change nothing, then the conditional UPDATE that
        // matches nothing. No sibling is renumbered.
        Assert.Equal(3, writes.Count);
        Assert.StartsWith("UPDATE \"Projects\"", writes[0], StringComparison.Ordinal);
        Assert.All(writes.Skip(1), write => Assert.StartsWith("UPDATE \"WorkItems\" AS \"w\"", write, StringComparison.Ordinal));
        Assert.Contains("\"w\".\"Version\" = @expected", writes[2], StringComparison.Ordinal);
        Assert.Equal(afterMove.Values.OrderBy(item => item.Id), afterStale.Values.OrderBy(item => item.Id), Same);
        Assert.Equal((WorkItemStatus.Ready, 3L), (unconditional.Status, unconditional.Version));
    }

    [Fact]
    public async Task Comments_links_dependencies_and_claims_do_not_advance_the_version_so_an_update_after_them_still_succeeds()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "KEEP");
        var item = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var other = await CreateAsync(client, project, new { title = "B", type = "Task", status = "Backlog", priority = "Medium" });

        var commented = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/comments", new { author = "tester", body = "note" });
        var linked = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/links", new AddWorkItemLinkRequest("pr", "https://github.com/acme/app/pull/1"));
        var blocked = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/dependencies", new AddWorkItemDependencyRequest(other.Id));
        using var caller = api.CreateCallerClient();
        var claimed = await PostAsync<WorkItemDto>(caller, $"/api/items/{item.Id}/claim", new ClaimWorkItemRequest("run-1", 60));
        var renewed = await PostAsync<WorkItemDto>(caller, $"/api/items/{item.Id}/claim/renew", new ClaimWorkItemRequest("run-1", 120));
        var released = await PostAsync<WorkItemDto>(caller, $"/api/items/{item.Id}/claim/release", new ReleaseWorkItemClaimRequest("run-1"));
        var updated = await PatchAsync(client, item.Id, new { status = "InProgress", expectedVersion = item.Version });
        // An update with the values the item already has is still an update.
        var sameValues = await PatchAsync(client, item.Id, new { status = "InProgress", title = "A" });
        var read = (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{item.Id}", Json))!;

        Assert.Equal([1L, 1L, 1L], new[] { commented.Version, linked.Version, blocked.Version });
        // The claim was really taken, renewed and given back, and none of the three is a write of the item.
        Assert.Equal(("run-1", "run-1", null), (claimed.ClaimMarker, renewed.ClaimMarker, released.ClaimMarker));
        Assert.True(renewed.ClaimExpiresAtUtc > claimed.ClaimExpiresAtUtc);
        Assert.Equal([1L, 1L, 1L], new[] { claimed.Version, renewed.Version, released.Version });
        Assert.Equal((WorkItemStatus.InProgress, 2L), (updated.Status, updated.Version));
        Assert.Equal((WorkItemStatus.InProgress, "A", updated.Order, 3L), (sameValues.Status, sameValues.Title, sameValues.Order, sameValues.Version));
        Assert.Equal(sameValues, read, Same);
        Assert.Single(updated.Comments);
        Assert.Single(updated.Links);
        Assert.Single(updated.Blockers);
    }

    [Fact]
    public async Task A_patch_that_names_no_field_is_refused_invalid_argument_and_writes_nothing_while_the_put_of_the_web_ui_is_as_it_was()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "NONE");
        var item = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        api.Commands.Clear();

        using var empty = await client.PatchAsJsonAsync($"/api/items/{item.Id}", new { });
        using var versionOnly = await client.PatchAsJsonAsync($"/api/items/{item.Id}", new { expectedVersion = 1 });
        using var staleVersionOnly = await client.PatchAsJsonAsync($"/api/items/{item.Id}", new { expectedVersion = 9 });
        var writes = Writes(api);
        var unchanged = await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{item.Id}", Json);
        // Another caller's guarded write still goes through.
        var guarded = await PatchAsync(client, item.Id, new { title = "B", expectedVersion = 1 });
        var put = await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{item.Id}", new { });

        await AssertErrorAsync(empty, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(versionOnly, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(staleVersionOnly, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(writes);
        Assert.Equal(item, unchanged, Same);
        Assert.Equal(("B", 2L), (guarded.Title, guarded.Version));
        Assert.Equal(("B", 3L), (put.Title, put.Version));
    }

    [Fact]
    public async Task An_empty_description_and_a_label_string_without_a_label_empty_those_fields_and_leave_the_rest()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, epic) = await CreateProjectAsync(client, "OLD");
        var item = await CreateAsync(client, project, new { title = "A", description = "Body", type = "Story", status = "Backlog", priority = "High", estimate = 5, labels = "api,backend", epicId = epic });

        // What update_work_item sends for the call it has always taken: the four fields that were required, and an empty string.
        var noDescription = await PatchAsync(client, item.Id, new { title = "A", type = "Story", status = "Backlog", priority = "High", description = "" });
        var noLabels = await PatchAsync(client, item.Id, new { title = "A", type = "Story", status = "Backlog", priority = "High", labels = "" });
        var relabelled = await PatchAsync(client, item.Id, new { labels = "x", description = "again" });
        var commasOnly = await PatchAsync(client, item.Id, new { labels = " , ", description = "   " });

        Assert.Equal(("", "api,backend", 5, epic, "A", 0, 2L), (noDescription.Description, noDescription.Labels, noDescription.Estimate, noDescription.EpicId, noDescription.Title, noDescription.Order, noDescription.Version));
        Assert.Equal(("", null, 5, epic, 3L), (noLabels.Description, noLabels.Labels, noLabels.Estimate, noLabels.EpicId, noLabels.Version));
        Assert.Empty(noLabels.LabelList);
        Assert.Equal(("again", "x"), (relabelled.Description, relabelled.Labels));
        Assert.Equal(("", null, 5, epic, 5L), (commasOnly.Description, commasOnly.Labels, commasOnly.Estimate, commasOnly.EpicId, commasOnly.Version));
        Assert.Empty(commasOnly.LabelList);
    }

    // A sibling the move read is deleted before the move renumbers it: the save of the siblings matches no row. Every
    // delete of an item waits for the move on MariaDB (both hold the project first), so this is a row that went
    // some other way; the move is rolled back whole and run again.
    [Fact]
    public async Task A_move_that_finds_a_sibling_deleted_while_it_renumbers_is_run_again_and_recorded_once()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "SIB");
        var a = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await CreateAsync(client, project, new { title = "B", type = "Task", status = "Backlog", priority = "Medium" });
        var entriesBefore = Entries(api).Count;

        api.SavesThatFindAnItemDeleted = 1;
        using var response = await client.PostAsJsonAsync($"/api/items/{b.Id}/move", new { status = "Backlog", order = 0, expectedVersion = 1 });
        var moved = await ItemsAsync(client);

        Assert.Equal(0, api.SavesThatFindAnItemDeleted);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The attempt that failed left nothing: the version advanced once, and the sibling it deleted is back, renumbered.
        Assert.Equal(2L, (await response.Content.ReadFromJsonAsync<WorkItemDto>(Json))!.Version);
        Assert.Equal((0, 2L, 1, 1L), (moved[b.Id].Order, moved[b.Id].Version, moved[a.Id].Order, moved[a.Id].Version));
        Assert.Equal([WorkItemChangeKinds.ItemMoved], Entries(api).Skip(entriesBefore));
    }

    [Fact]
    public async Task A_move_that_finds_a_sibling_deleted_on_every_attempt_is_refused_busy_and_moves_and_records_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "SIB");
        var a = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await CreateAsync(client, project, new { title = "B", type = "Task", status = "Backlog", priority = "Medium" });
        var entriesBefore = Entries(api).Count;

        api.SavesThatFindAnItemDeleted = WorkItemChangeLog.DeadlockAttempts + 2;
        using var response = await client.PostAsJsonAsync($"/api/items/{b.Id}/move", new { status = "Backlog", order = 0, expectedVersion = 1 });
        var attemptsLeft = api.SavesThatFindAnItemDeleted;
        api.SavesThatFindAnItemDeleted = 0;
        var after = await ItemsAsync(client);
        var entriesAfter = Entries(api).Count;
        // Nothing of the refused move is left, so the same move, with the same version, goes through.
        var next = await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/move", new { status = "Backlog", order = 0, expectedVersion = 1 });
        var moved = await ItemsAsync(client);

        // The first attempt and two more, and then a refusal a caller can read.
        Assert.Equal(2, attemptsLeft);
        var message = await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, KanbanErrorCodes.Busy);
        Assert.Contains("Nothing was changed", message, StringComparison.Ordinal);
        // The item kept its place and its version, the sibling is there with its place, and the change feed got no entry.
        Assert.Equal(b, after[b.Id], Same);
        Assert.Equal(a, after[a.Id], Same);
        Assert.Equal(entriesBefore, entriesAfter);
        Assert.Equal((0, 1, 2L, 1L), (moved[b.Id].Order, moved[a.Id].Order, next.Version, moved[a.Id].Version));
        Assert.Equal([WorkItemChangeKinds.ItemMoved], Entries(api).Skip(entriesBefore));
    }

    // A save that fails for any other reason than a row that went is not run again: the move answers the error and
    // leaves nothing behind.
    [Fact]
    public async Task A_move_whose_renumbering_of_the_siblings_fails_moves_nothing_and_records_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "SIB");
        var a = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await CreateAsync(client, project, new { title = "B", type = "Task", status = "Backlog", priority = "Medium" });
        var c = await CreateAsync(client, project, new { title = "C", type = "Task", status = "Ready", priority = "Medium" });
        var entriesBefore = Entries(api).Count;

        // The item is written by an UPDATE of its own; apart from the entry of the change feed, the only save of
        // a move is the one of its siblings, in the same transaction.
        api.FailNextSave = true;
        using var response = await client.PostAsJsonAsync($"/api/items/{b.Id}/move", new { status = "Ready", order = 0, expectedVersion = 1 });
        var failed = !api.FailNextSave;
        var after = await ItemsAsync(client);
        var entriesAfter = Entries(api).Count;
        // Nothing of the failed move is left, so the same move, with the same version, goes through.
        var next = await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/move", new { status = "Ready", order = 0, expectedVersion = 1 });
        var moved = await ItemsAsync(client);

        // It failed once, and was not run again.
        Assert.True(failed);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // The item kept its column, its place, its version and its stamp, the siblings theirs, and the change feed
        // got no entry.
        Assert.Equal(b, after[b.Id], Same);
        Assert.Equal(a, after[a.Id], Same);
        Assert.Equal(c, after[c.Id], Same);
        Assert.Equal(entriesBefore, entriesAfter);
        Assert.Equal((WorkItemStatus.Ready, 0, 2L, 1, 1L), (next.Status, moved[b.Id].Order, next.Version, moved[c.Id].Order, moved[c.Id].Version));
        Assert.Equal([WorkItemChangeKinds.ItemMoved], Entries(api).Skip(entriesBefore));
    }

    // The update of a status takes the same way out: its save finds a row gone, and it is run again.
    [Fact]
    public async Task An_update_of_the_status_whose_save_finds_a_row_deleted_is_run_again_and_recorded_once()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "UPD");
        var a = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var entriesBefore = Entries(api).Count;

        api.SavesThatFindAnItemDeleted = 1;
        using var response = await client.PatchAsJsonAsync($"/api/items/{a.Id}", new { status = "Ready", expectedVersion = 1 });
        var updated = (await ItemsAsync(client))[a.Id];

        Assert.Equal(0, api.SavesThatFindAnItemDeleted);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((WorkItemStatus.Ready, 2L), (updated.Status, updated.Version));
        Assert.Equal([WorkItemChangeKinds.ItemUpdated], Entries(api).Skip(entriesBefore));
    }

    private static List<string> Entries(ApiApplication api)
    {
        using var dbContext = api.OpenDatabase();
        return dbContext.WorkItemChanges.OrderBy(change => change.Sequence).Select(change => change.Kind).ToList();
    }

    // Eight callers move eight items into one column at once, each to the top and each with the version it read,
    // while two more change their status to it, two are created in it and two of its items are deleted: every write
    // succeeds, and no two items of the column share a place. Every move renumbers the column in the transaction
    // that moves its item, behind the row of the project, which the deletes hold as well.
    [Fact]
    public async Task Moves_updates_creates_and_deletes_in_one_column_at_the_same_moment_leave_every_item_a_place_of_its_own()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "RACE");
        var seeded = new List<WorkItemDto>();
        foreach (var index in Enumerable.Range(0, 14))
        {
            seeded.Add(await CreateAsync(client, project, new { title = $"Item {index}", type = "Task", status = index is < 2 or >= 12 ? "Ready" : "Backlog", priority = "Medium" }));
        }

        using var entries = api.OpenDatabase();
        var entriesBefore = entries.WorkItemChanges.Count();

        var moves = seeded.Skip(2).Take(8).Select(item => Task.Run(() =>
            client.PostAsJsonAsync($"/api/items/{item.Id}/move", new { status = "Ready", order = 0, expectedVersion = item.Version })));
        var updates = seeded.Skip(10).Take(2).Select(item => Task.Run(() =>
            client.PatchAsJsonAsync($"/api/items/{item.Id}", new { status = "Ready", expectedVersion = item.Version })));
        var creates = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            client.PostAsJsonAsync("/api/items", new { projectId = project, title = $"New {index}", type = "Task", status = "Ready", priority = "Medium" })));
        var deletes = seeded.Skip(12).Select(item => Task.Run(() => client.DeleteAsync($"/api/items/{item.Id}")));
        var responses = await Task.WhenAll(moves.Concat(updates).Concat(creates).Concat(deletes));

        var after = await ItemsAsync(client);
        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}"));
        var ready = after.Values.Where(item => item.Status == WorkItemStatus.Ready).ToList();
        Assert.Equal(14, ready.Count);
        Assert.Equal(14, ready.Select(item => item.Order).Distinct().Count());
        Assert.DoesNotContain(seeded.Skip(12), item => after.ContainsKey(item.Id));
        // The two items that were in the column and were only displaced keep their version and their stamp.
        Assert.All(seeded.Take(2), item => Assert.Equal((1L, item.UpdatedAtUtc), (after[item.Id].Version, after[item.Id].UpdatedAtUtc)));
        // One entry per write: eight moves, two updates, two creates, two deletes; none for a displaced sibling.
        using var stored = api.OpenDatabase();
        var written = stored.WorkItemChanges.OrderBy(change => change.Sequence).Skip(entriesBefore).Select(change => change.Kind).ToList();
        Assert.Equal(
            (8, 2, 2, 2, 14),
            (written.Count(kind => kind == WorkItemChangeKinds.ItemMoved), written.Count(kind => kind == WorkItemChangeKinds.ItemUpdated),
                written.Count(kind => kind == WorkItemChangeKinds.ItemCreated), written.Count(kind => kind == WorkItemChangeKinds.ItemDeleted), written.Count));
    }

    [Fact]
    public async Task Labels_are_written_as_a_list_or_a_string_and_read_back_as_both()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "LBL");
        // 60 labels of 12 characters: 779 characters joined, more than three times the old cap of 240.
        var many = Enumerable.Range(0, 60).Select(index => $"label-{index:D6}").ToArray();

        var fromString = await CreateAsync(client, project, new { title = "String", type = "Task", status = "Backlog", priority = "Medium", labels = " api, Backend ,api,," });
        var fromList = await CreateAsync(client, project, new { title = "List", type = "Task", status = "Backlog", priority = "Medium", labelList = new[] { " ui ", "UI", "two words" } });
        var updated = await PatchAsync(client, fromString.Id, new { labels = many });
        var read = (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{fromString.Id}", Json))!;
        var listed = (await ItemsAsync(client))[fromString.Id];
        var filtered = await client.GetFromJsonAsync<List<WorkItemDto>>("/api/items?label=LABEL-000059", Json);
        // The web UI's update: a comma-separated string, with every field.
        var put = await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{fromString.Id}", new { epicId = (Guid?)null, title = "String", description = (string?)null, type = "Task", status = "Backlog", priority = "Medium", estimate = (int?)null, labels = "x,y" });

        AssertLabels(fromString, "api", "Backend");
        AssertLabels(fromList, "ui", "two words");
        Assert.True(string.Join(',', many).Length > 240);
        AssertLabels(updated, many);
        AssertLabels(read, many);
        AssertLabels(listed, many);
        Assert.Equal(fromString.Id, Assert.Single(filtered!).Id);
        AssertLabels(put, "x", "y");
    }

    [Fact]
    public void Labels_stored_before_the_list_existed_read_back_as_the_same_labels()
    {
        var item = new WorkItem { Title = "Old", Labels = "api, Backend ,two words,,100%" };
        var none = new WorkItem { Title = "None", Labels = null };

        var dto = WorkItemDtoMapper.ToDto(item, DateTimeOffset.UtcNow, new AppOptions());

        // The string exactly as stored, and its labels.
        Assert.Equal("api, Backend ,two words,,100%", dto.Labels);
        Assert.Equal(["api", "Backend", "two words", "100%"], dto.LabelList);
        Assert.Empty(WorkItemDtoMapper.ToDto(none, DateTimeOffset.UtcNow, new AppOptions()).LabelList);
    }

    [Fact]
    public async Task Labels_that_cannot_be_stored_are_refused_invalid_argument_and_write_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "BAD");
        var item = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium", labels = "keep" });
        // 4001 characters joined: one more than fits.
        var tooLong = Enumerable.Range(0, 363).Select(index => $"label-{index:D4}").Append("12345678").ToArray();
        var fits = tooLong[..^1].Append("1234567").ToArray();

        foreach (var labels in new object[] { new[] { "a,b" }, new[] { "ok", " " }, new object[] { "ok", 1 }, tooLong, 5 })
        {
            using var refused = await client.PatchAsJsonAsync($"/api/items/{item.Id}", new { labels });
            await AssertErrorAsync(refused, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        }

        using var both = await client.PostAsJsonAsync("/api/items", new { projectId = project, title = "B", type = "Task", status = "Backlog", priority = "Medium", labels = "a", labelList = new[] { "b" } });
        using var longTitle = await client.PostAsJsonAsync("/api/items", new { projectId = project, title = new string('t', 241), type = "Task", status = "Backlog", priority = "Medium" });
        var unchanged = await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{item.Id}", Json);
        var longest = await PatchAsync(client, item.Id, new { labels = fits });

        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longTitle.StatusCode);
        Assert.Equal(item, unchanged, Same);
        Assert.Equal(WorkItemLimits.MaxLabelsLength, longest.Labels!.Length);
        Assert.Equal(fits, longest.LabelList);
    }

    [Fact]
    public async Task Estimate_and_version_are_on_the_item_from_get_list_page_board_and_search_also_when_the_estimate_is_not_set()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "EST");
        var with = await CreateAsync(client, project, new { title = "needle with", type = "Task", status = "Backlog", priority = "Medium", estimate = 3 });
        var without = await CreateAsync(client, project, new { title = "needle without", type = "Task", status = "Backlog", priority = "Medium" });
        await PatchAsync(client, with.Id, new { priority = "High" });

        foreach (var path in new[] { $"/api/items?projectId={project}", $"/api/items?projectId={project}&limit=10", $"/api/projects/{project}", "/api/items/search?query=needle", $"/api/items/{with.Id}", $"/api/items/{without.Id}" })
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync(path));
            var root = json.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList()
                : root.TryGetProperty("items", out var nested) ? nested.EnumerateArray().ToList()
                : [root];
            Assert.NotEmpty(items);
            Assert.All(items, item =>
            {
                var isWith = item.GetProperty("id").GetGuid() == with.Id;
                Assert.True(item.TryGetProperty("estimate", out var estimate), path);
                Assert.Equal(isWith ? JsonValueKind.Number : JsonValueKind.Null, estimate.ValueKind);
                Assert.Equal(isWith ? 2 : 1, item.GetProperty("version").GetInt64());
            });
        }
    }

    [Fact]
    public async Task The_put_of_the_web_ui_ignores_fields_it_does_not_know_and_the_patch_refuses_them()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "PUT");
        var item = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });

        var put = await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{item.Id}", new { title = "B", comments = Array.Empty<object>(), id = item.Id });
        using var patch = await client.PatchAsJsonAsync($"/api/items/{item.Id}", new { title = "C", tittle = "D" });
        using var notAnObject = await client.PatchAsJsonAsync($"/api/items/{item.Id}", new[] { 1 });
        using var badVersion = await client.PatchAsJsonAsync($"/api/items/{item.Id}", new { title = "C", expectedVersion = "2" });

        Assert.Equal(("B", 2L), (put.Title, put.Version));
        Assert.Contains("tittle", await AssertErrorAsync(patch, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument), StringComparison.Ordinal);
        await AssertErrorAsync(notAnObject, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(badVersion, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Equal(put, await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{item.Id}", Json), Same);
    }

    [Fact]
    public async Task An_unknown_item_is_refused_not_found_by_update_and_move_with_and_without_an_expected_version()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var unknown = Guid.NewGuid();

        foreach (var expectedVersion in new long?[] { null, 1 })
        {
            using var patch = await client.PatchAsJsonAsync($"/api/items/{unknown}", new { title = "x", expectedVersion });
            using var put = await client.PutAsJsonAsync($"/api/items/{unknown}", new { title = "x", expectedVersion });
            using var move = await client.PostAsJsonAsync($"/api/items/{unknown}/move", new { status = "Done", order = 0, expectedVersion });

            Assert.Contains(unknown.ToString(), await AssertErrorAsync(patch, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound), StringComparison.Ordinal);
            await AssertErrorAsync(put, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
            await AssertErrorAsync(move, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        }
    }

    [Fact]
    public async Task An_epic_is_checked_only_when_the_update_names_one_and_a_refused_update_takes_its_version_back()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, epic) = await CreateProjectAsync(client, "EPIC");
        var archived = await PostAsync<EpicDto>(client, $"/api/projects/{project}/epics", new { name = "Archived" });
        var done = await CreateAsync(client, project, new { title = "Done", type = "Task", status = "Done", priority = "Medium", epicId = archived.Id });
        var open = await CreateAsync(client, project, new { title = "Open", type = "Task", status = "Backlog", priority = "Medium", epicId = archived.Id });
        await Task.Delay(20);

        await SendAsync<EpicDto>(client, HttpMethod.Put, $"/api/epics/{archived.Id}", new { name = "Archived", description = (string?)null, isArchived = true });
        var afterArchive = await ItemsAsync(client, "?includeArchivedEpics=true");
        // The finished item stays in the archived epic, and a change that leaves the epic alone is not refused for it.
        var relabelled = await PatchAsync(client, done.Id, new { labels = new[] { "shipped" } });
        using var intoArchived = await client.PatchAsJsonAsync($"/api/items/{open.Id}", new { epicId = archived.Id, title = "Moved" });
        var afterRefusal = (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{open.Id}", Json))!;
        var intoActive = await PatchAsync(client, open.Id, new { epicId = epic });
        await client.DeleteAsync($"/api/epics/{epic}");
        var afterDelete = (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{open.Id}", Json))!;

        // Archiving took the unfinished item out of the epic: a change of that item.
        Assert.Equal((null, 2L), (afterArchive[open.Id].EpicId, afterArchive[open.Id].Version));
        Assert.True(afterArchive[open.Id].UpdatedAtUtc > open.UpdatedAtUtc);
        Assert.Equal((archived.Id, 1L, done.UpdatedAtUtc), (afterArchive[done.Id].EpicId, afterArchive[done.Id].Version, afterArchive[done.Id].UpdatedAtUtc));
        Assert.Equal((archived.Id, "shipped", 2L), (relabelled.EpicId, relabelled.Labels, relabelled.Version));
        await AssertErrorAsync(intoArchived, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Equal(afterArchive[open.Id], afterRefusal, Same);
        Assert.Equal((epic, 3L), (intoActive.EpicId, intoActive.Version));
        // So does deleting the epic.
        Assert.Equal((null, null, 4L), (afterDelete.EpicId, afterDelete.Epic, afterDelete.Version));
        Assert.True(afterDelete.UpdatedAtUtc > intoActive.UpdatedAtUtc);
    }

    // On MariaDB a write that puts an item into the epic reads the epic's row and then adds to the index on the epic;
    // an archive or a delete that took the items first and the epic's row last deadlocked with it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archiving_and_deleting_an_epic_hold_the_project_then_the_epic_before_they_take_its_items_out(bool delete)
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, epic) = await CreateProjectAsync(client, "LOCK");
        var item = await CreateAsync(client, project, new { title = "Open", type = "Task", status = "Backlog", priority = "Medium", epicId = epic });
        var entriesBefore = Entries(api).Count;
        api.Commands.Clear();

        using var response = delete
            ? await client.DeleteAsync($"/api/epics/{epic}")
            : await client.PutAsJsonAsync($"/api/epics/{epic}", new { name = "Epic", isArchived = true });
        var writes = Writes(api);
        var after = (await client.GetFromJsonAsync<WorkItemDto>($"/api/items/{item.Id}", Json))!;

        Assert.True(response.IsSuccessStatusCode);
        Assert.StartsWith("UPDATE \"Projects\"", writes[0], StringComparison.Ordinal);
        Assert.StartsWith("UPDATE \"Epics\"", writes[1], StringComparison.Ordinal);
        Assert.StartsWith("UPDATE \"WorkItems\"", writes[2], StringComparison.Ordinal);
        // What the write does is as it was: the item is out of the epic, one version on, with one entry in the feed.
        Assert.Equal((null, 2L), (after.EpicId, after.Version));
        Assert.Equal([WorkItemChangeKinds.ItemUpdated], Entries(api).Skip(entriesBefore));
    }

    // The save of an update that names an epic reads the epic's row while it holds the item's; the archive of the
    // epic holds the epic's row and then takes the items. Behind the project's row the two run one after the other.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_update_that_names_an_epic_holds_the_project_first_and_one_that_names_neither_epic_nor_status_does_not(bool set)
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, epic) = await CreateProjectAsync(client, "HOLD");
        var item = await CreateAsync(client, project, new { title = "Item", type = "Task", status = "Backlog", priority = "Medium", epicId = set ? (Guid?)null : epic });
        api.Commands.Clear();

        var updated = await PatchAsync(client, item.Id, new { epicId = set ? (Guid?)epic : null });
        var writes = Writes(api);
        api.Commands.Clear();
        await PatchAsync(client, item.Id, new { title = "Renamed" });

        Assert.Equal((set ? epic : null, 2L), (updated.EpicId, updated.Version));
        Assert.StartsWith("UPDATE \"Projects\"", writes[0], StringComparison.Ordinal);
        Assert.StartsWith("UPDATE \"WorkItems\" AS \"w\"", writes[1], StringComparison.Ordinal);
        Assert.Equal(4, writes.Count);
        Assert.DoesNotContain(Writes(api), write => write.StartsWith("UPDATE \"Projects\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_status_puts_the_item_at_the_end_of_that_column_and_the_same_status_leaves_its_place()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var (project, _) = await CreateProjectAsync(client, "COL");
        var a = await CreateAsync(client, project, new { title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        await CreateAsync(client, project, new { title = "B", type = "Task", status = "Ready", priority = "Medium" });
        await CreateAsync(client, project, new { title = "C", type = "Task", status = "Ready", priority = "Medium" });

        var same = await PatchAsync(client, a.Id, new { status = "Backlog" });
        var ready = await PatchAsync(client, a.Id, new { status = "Ready" });

        Assert.Equal((WorkItemStatus.Backlog, 0, 2L), (same.Status, same.Order, same.Version));
        Assert.Equal((WorkItemStatus.Ready, 2, 3L), (ready.Status, ready.Order, ready.Version));
    }

    // The item carries the labels both ways: the comma-separated string it always had, and the list.
    private static void AssertLabels(WorkItemDto item, params string[] labels)
    {
        Assert.Equal(string.Join(',', labels), item.Labels);
        Assert.Equal(labels, item.LabelList);
    }

    // Two reads of one item: equal when every field a write could have changed is equal.
    private static readonly IEqualityComparer<WorkItemDto?> Same = new ItemComparer();

    private sealed class ItemComparer : IEqualityComparer<WorkItemDto?>
    {
        public bool Equals(WorkItemDto? x, WorkItemDto? y) => Fields(x) == Fields(y);

        public int GetHashCode(WorkItemDto? item) => Fields(item).GetHashCode();

        private static string Fields(WorkItemDto? item) => item is null
            ? string.Empty
            : JsonSerializer.Serialize(new { item.Id, item.EpicId, item.Title, item.Description, item.Type, item.Status, item.Priority, item.Order, item.Estimate, item.Labels, item.LabelList, item.UpdatedAtUtc, item.Version });
    }

    private static List<string> Writes(ApiApplication api) =>
        api.Commands.Where(text => text.StartsWith("UPDATE", StringComparison.Ordinal) || text.StartsWith("INSERT", StringComparison.Ordinal) || text.StartsWith("DELETE", StringComparison.Ordinal)).ToList();

    private static async Task<(Guid Project, Guid Epic)> CreateProjectAsync(HttpClient client, string key)
    {
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key });
        var epic = await PostAsync<EpicDto>(client, $"/api/projects/{project.Id}/epics", new { name = "Epic" });
        return (project.Id, epic.Id);
    }

    // Adds the project id to the fields of a new item.
    private static Task<WorkItemDto> CreateAsync(HttpClient client, Guid projectId, object fields)
    {
        var body = JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["projectId"] = projectId;
        return PostAsync<WorkItemDto>(client, "/api/items", body);
    }

    private static Task<WorkItemDto> PatchAsync(HttpClient client, Guid itemId, object body) =>
        SendAsync<WorkItemDto>(client, HttpMethod.Patch, $"/api/items/{itemId}", body);

    private static async Task<Dictionary<Guid, WorkItemDto>> ItemsAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items{query}", Json))!.ToDictionary(item => item.Id);

    private static async Task<string> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode} {body}");
        var error = JsonSerializer.Deserialize<KanbanErrorDto>(body, Json)!;
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error.Message;
    }
}
