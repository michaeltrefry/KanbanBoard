// Hosted acceptance script for the factory work-source tools (epic sc-25857).
// It talks to a running KanbanBoard MCP server exactly as the orchestrator does: MCP over HTTP with a bearer token.
//
//   KANBAN_MCP_URL                 MCP endpoint, for example https://kanban-mcp.example.net/mcp
//   KANBAN_FACTORY_TOKEN           full-scope token of the factory service identity
//   KANBAN_FACTORY_READ_TOKEN      optional: read-scope token of the same identity; when set, the script also checks
//                                  that the write tools refuse it with scope_denied and that it can use every read
//                                  tool. Without it epic acceptance test 7 is skipped.
//   KANBAN_ACCEPTANCE_PROJECT_ID   optional: an existing project in which the finer-grained checks create (and
//                                  afterwards delete) their scratch work items. Without it the script creates a
//                                  scratch project for them.
//
// The script first runs its finer-grained checks, tool by tool, and then the eight acceptance tests of the epic,
// literally and in order, in a scratch project it creates with a repository URL. It ends with one line per epic
// acceptance test (PASS, FAIL or SKIP), which can be pasted onto the epic.
//
// Every scratch project the script creates (keys ACC, ACI and ACE plus 20 random hex characters) is left archived,
// which keeps it out of list_projects; the script can be run again against the same database. It writes only into
// those scratch projects, and into KANBAN_ACCEPTANCE_PROJECT_ID when that is set.
//
//   dotnet run --project tools/KanbanBoard.FactoryAcceptance
//
// Exit code 0 when every check passes, 1 when a check fails, 2 when the environment is incomplete.
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var mcpUrl = Environment.GetEnvironmentVariable("KANBAN_MCP_URL");
var token = Environment.GetEnvironmentVariable("KANBAN_FACTORY_TOKEN");
var projectId = Environment.GetEnvironmentVariable("KANBAN_ACCEPTANCE_PROJECT_ID");
var readToken = Environment.GetEnvironmentVariable("KANBAN_FACTORY_READ_TOKEN");
if (string.IsNullOrWhiteSpace(mcpUrl) || string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Set KANBAN_MCP_URL and KANBAN_FACTORY_TOKEN.");
    return 2;
}

using var httpClient = new HttpClient();
await using var client = await ConnectAsync(httpClient, token);

var failures = 0;
var run = Guid.NewGuid().ToString("N")[..8];
var markerA = $"acceptance-{run}-a";
var markerB = $"acceptance-{run}-b";
var epicTests = new List<(int Number, string Text, string Result)>();

// Without a project of the caller's, the finer-grained checks get a scratch project of their own.
string? ownProjectId = null;
if (string.IsNullOrWhiteSpace(projectId))
{
    var ownProject = await CallAsync("create_project", new()
    {
        ["name"] = $"Factory acceptance scratch items {run}", ["key"] = $"ACI{Guid.NewGuid().ToString("N")[..20]}".ToUpperInvariant()
    });
    if (ownProject.Code is not null)
    {
        Console.Error.WriteLine($"Could not create the scratch project: {ownProject.Code}: {ownProject.Message}");
        return 1;
    }

    projectId = ownProjectId = StringField(ownProject.Body, "id")!;
    Console.WriteLine($"Scratch project {projectId}");
}

var created = await CallAsync("create_work_item", new()
{
    ["projectId"] = projectId,
    ["title"] = $"Factory acceptance scratch item {run}",
    ["type"] = "Task"
});
if (created.Code is not null)
{
    Console.Error.WriteLine($"Could not create the scratch work item: {created.Code}: {created.Message}");
    return 1;
}

var itemId = created.Body.GetProperty("id").GetString()!;
Console.WriteLine($"Scratch work item {itemId}");
try
{
    // 1. Claim an unclaimed item, and read the claim back.
    var first = await ClaimAsync(markerA, 60);
    Check("claim_work_item on an unclaimed item succeeds", first.Code is null, first);
    var claimant = first.Code is null ? StringField(first.Body, "claimant") : null;
    Check("the claim records a claimant, the marker and an expiry",
        !string.IsNullOrEmpty(claimant) &&
        StringField(first.Body, "claim_marker") == markerA &&
        StringField(first.Body, "claim_expires_at_utc") is not null,
        first);
    var read = await CallAsync("get_work_item", new() { ["itemId"] = itemId });
    Check("get_work_item returns the same claimant, marker and expiry",
        read.Code is null &&
        StringField(read.Body, "claimant") == claimant &&
        StringField(read.Body, "claim_marker") == markerA &&
        StringField(read.Body, "claim_expires_at_utc") == StringField(first.Body, "claim_expires_at_utc"),
        read);

    // 2. A second claim under another marker is refused.
    var second = await ClaimAsync(markerB, 60);
    Check("a second claim with another marker is refused already_claimed", second.Code == "already_claimed", second);

    // 3. The holder renews: the expiry moves later.
    var renewed = await CallAsync("renew_work_item_claim", new() { ["id"] = itemId, ["marker"] = markerA, ["ttl_seconds"] = 600 });
    Check("renew_work_item_claim with the holder's marker extends claim_expires_at_utc",
        renewed.Code is null &&
        StringField(renewed.Body, "claim_marker") == markerA &&
        ExpiryField(renewed.Body) is { } renewedExpiry &&
        ExpiryField(first.Body) is { } firstExpiry &&
        renewedExpiry > firstExpiry,
        renewed);

    // 4. The holder releases: the claim fields are null, and another marker can claim at once.
    var released = await CallAsync("release_work_item_claim", new() { ["id"] = itemId, ["marker"] = markerA });
    Check("release_work_item_claim with the holder's marker returns null claim fields",
        released.Code is null && ClaimFields.All(field => IsNullField(released.Body, field)),
        released);
    var afterRelease = await ClaimAsync(markerB, 60);
    Check("after the release, a claim with another marker succeeds",
        afterRelease.Code is null && StringField(afterRelease.Body, "claim_marker") == markerB,
        afterRelease);

    // 5. A short claim lapses, and another marker then succeeds.
    var shortClaim = await ClaimAsync(markerB, 2);
    Check("the holder can claim again with a short ttl", shortClaim.Code is null, shortClaim);
    await Task.Delay(TimeSpan.FromSeconds(4));
    var afterLapse = await ClaimAsync(markerA, 60);
    Check("after the short claim lapses, another marker succeeds",
        afterLapse.Code is null && StringField(afterLapse.Body, "claim_marker") == markerA,
        afterLapse);

    // 6. A read-scope token is refused by all three claim tools, and the claim is left as it was.
    if (string.IsNullOrWhiteSpace(readToken))
    {
        Console.WriteLine("SKIP  read-scope refusal of the claim tools (KANBAN_FACTORY_READ_TOKEN is not set)");
    }
    else
    {
        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        foreach (var tool in new[] { "claim_work_item", "renew_work_item_claim", "release_work_item_claim" })
        {
            var arguments = new Dictionary<string, object?> { ["id"] = itemId, ["marker"] = markerA };
            if (tool != "release_work_item_claim")
            {
                arguments["ttl_seconds"] = 60;
            }

            var refused = await CallAsync(tool, arguments, readClient);
            Check($"{tool} with the read-scope token is refused scope_denied", refused.Code == "scope_denied", refused);
        }

        var afterRefusals = await CallAsync("get_work_item", new() { ["itemId"] = itemId });
        Check("the refused read-scope calls left the claim unchanged",
            afterRefusals.Code is null &&
            StringField(afterRefusals.Body, "claim_marker") == markerA &&
            StringField(afterRefusals.Body, "claim_expires_at_utc") == StringField(afterLapse.Body, "claim_expires_at_utc"),
            afterRefusals);
    }
}
finally
{
    var deleted = await CallAsync("delete_work_item", new() { ["itemId"] = itemId });
    if (deleted.Code is not null)
    {
        Console.Error.WriteLine($"Could not delete scratch work item {itemId}: {deleted.Code}: {deleted.Message}");
        failures++;
    }
}

// Dependencies: C is blocked by B, B is blocked by A.
var chain = new Dictionary<string, string>();
try
{
    foreach (var name in new[] { "A", "B", "C" })
    {
        var scratch = await CallAsync("create_work_item", new()
        {
            ["projectId"] = projectId,
            ["title"] = $"Factory acceptance dependency item {name} {run}",
            ["type"] = "Task"
        });
        if (scratch.Code is not null)
        {
            Console.Error.WriteLine($"Could not create dependency scratch item {name}: {scratch.Code}: {scratch.Message}");
            return 1;
        }

        chain[name] = scratch.Body.GetProperty("id").GetString()!;
    }

    Console.WriteLine($"Dependency scratch items A {chain["A"]}, B {chain["B"]}, C {chain["C"]}");

    // 7. Add the two edges; reads report them.
    var cOnB = await CallAsync("add_work_item_dependency", new() { ["id"] = chain["C"], ["blocked_by_id"] = chain["B"] });
    Check("add_work_item_dependency (C blocked by B) returns C with B as its blocker",
        cOnB.Code is null && ReferencedIds(cOnB.Body, "blockers").SequenceEqual([chain["B"]]), cOnB);
    var bOnA = await CallAsync("add_work_item_dependency", new() { ["id"] = chain["B"], ["blocked_by_id"] = chain["A"] });
    Check("add_work_item_dependency (B blocked by A) succeeds", bOnA.Code is null, bOnA);
    var readB = await CallAsync("get_work_item", new() { ["itemId"] = chain["B"] });
    Check("get_work_item on B returns blocker A and dependent C",
        readB.Code is null &&
        ReferencedIds(readB.Body, "blockers").SequenceEqual([chain["A"]]) &&
        ReferencedIds(readB.Body, "dependents").SequenceEqual([chain["C"]]),
        readB);

    // 8. Only A is unblocked.
    var unblocked = await CallAsync("list_work_items", new() { ["projectId"] = projectId, ["unblocked_only"] = true });
    Check("list_work_items with unblocked_only returns A but neither B nor C",
        unblocked.Code is null && ListedOf(unblocked.Body, chain).SequenceEqual(["A"]), unblocked);

    // 9. Closing the loop is refused and changes nothing.
    var cycle = await CallAsync("add_work_item_dependency", new() { ["id"] = chain["A"], ["blocked_by_id"] = chain["C"] });
    Check("add_work_item_dependency (A blocked by C) is refused cycle", cycle.Code == "cycle", cycle);
    var readA = await CallAsync("get_work_item", new() { ["itemId"] = chain["A"] });
    Check("the refused call left A without blockers", readA.Code is null && ReferencedIds(readA.Body, "blockers").Length == 0, readA);

    // 10. A read-scope token is refused by both dependency tools.
    if (string.IsNullOrWhiteSpace(readToken))
    {
        Console.WriteLine("SKIP  read-scope refusal of the dependency tools (KANBAN_FACTORY_READ_TOKEN is not set)");
    }
    else
    {
        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        foreach (var tool in new[] { "add_work_item_dependency", "remove_work_item_dependency" })
        {
            var refused = await CallAsync(tool, new() { ["id"] = chain["C"], ["blocked_by_id"] = chain["B"] }, readClient);
            Check($"{tool} with the read-scope token is refused scope_denied", refused.Code == "scope_denied", refused);
        }

        var readUnblocked = await CallAsync("list_work_items", new() { ["projectId"] = projectId, ["unblocked_only"] = true }, readClient);
        Check("the read-scope token can list with unblocked_only, and C is still blocked",
            readUnblocked.Code is null && ListedOf(readUnblocked.Body, chain).SequenceEqual(["A"]), readUnblocked);
    }

    // 11. Once A is Done, B is unblocked and C is not.
    var done = await CallAsync("move_work_item", new() { ["itemId"] = chain["A"], ["status"] = "Done" });
    Check("move_work_item moves A to Done", done.Code is null, done);
    var afterDone = await CallAsync("list_work_items", new() { ["projectId"] = projectId, ["unblocked_only"] = true });
    Check("after A is Done, list_work_items with unblocked_only returns B and still not C",
        afterDone.Code is null && ListedOf(afterDone.Body, chain).SequenceEqual(["A", "B"]), afterDone);
    var listedB = afterDone.Code is null
        ? afterDone.Body.EnumerateArray().FirstOrDefault(item => StringField(item, "id") == chain["B"])
        : default;
    Check("the listed B reports its blocker A with status Done",
        listedB.ValueKind == JsonValueKind.Object &&
        listedB.GetProperty("blockers").EnumerateArray().Any(blocker =>
            StringField(blocker, "id") == chain["A"] && StringField(blocker, "status") == "Done"),
        afterDone);

    // 12. Removing an edge unblocks its item.
    var removed = await CallAsync("remove_work_item_dependency", new() { ["id"] = chain["C"], ["blocked_by_id"] = chain["B"] });
    Check("remove_work_item_dependency (C blocked by B) returns C without blockers",
        removed.Code is null && ReferencedIds(removed.Body, "blockers").Length == 0, removed);
    var afterRemove = await CallAsync("list_work_items", new() { ["projectId"] = projectId, ["unblocked_only"] = true });
    Check("after the removal, list_work_items with unblocked_only returns C",
        afterRemove.Code is null && ListedOf(afterRemove.Body, chain).SequenceEqual(["A", "B", "C"]), afterRemove);
}
finally
{
    foreach (var (name, id) in chain)
    {
        var deleted = await CallAsync("delete_work_item", new() { ["itemId"] = id });
        if (deleted.Code is not null)
        {
            Console.Error.WriteLine($"Could not delete dependency scratch item {name} {id}: {deleted.Code}: {deleted.Message}");
            failures++;
        }
    }
}

// Polling: five items share a label, a sixth has a label that only starts with it.
var label = $"acceptance-{run}";
var polled = new List<JsonElement>();
string? decoyId = null;
try
{
    foreach (var (title, labels) in Enumerable.Range(1, 5).Select(number => ($"polled {number}", $"scratch,{label}")).Append(("decoy", $"{label}-other")))
    {
        var scratch = await CallAsync("create_work_item", new()
        {
            ["projectId"] = projectId,
            ["title"] = $"Factory acceptance {title} {run}",
            ["type"] = "Task",
            ["labels"] = labels
        });
        if (scratch.Code is not null)
        {
            Console.Error.WriteLine($"Could not create polling scratch item '{title}': {scratch.Code}: {scratch.Message}");
            return 1;
        }

        if (title == "decoy")
        {
            decoyId = StringField(scratch.Body, "id");
        }
        else
        {
            polled.Add(scratch.Body);
        }
    }

    var polledIds = polled.Select(item => StringField(item, "id")!).ToList();
    Console.WriteLine($"Polling scratch items {string.Join(", ", polledIds)}");

    // 13. A filtered walk with a small page size returns each labelled item exactly once and never the decoy.
    var filter = new Dictionary<string, object?> { ["projectId"] = projectId, ["label"] = label.ToUpperInvariant() };
    var (walked, pageCount, walkFailure) = await WalkAsync(new(filter) { ["limit"] = 2 });
    Check("a paged list_work_items walk (limit 2) filtered by label returns each labelled item exactly once",
        walkFailure is null && walked.Order().SequenceEqual(polledIds.Order()) && pageCount == 3,
        walkFailure ?? new ToolOutcome("error", $"walked {string.Join(", ", walked)} in {pageCount} page(s)", default));
    var badCursor = await CallAsync("list_work_items", new(filter) { ["cursor"] = "not-a-cursor" });
    Check("list_work_items with a malformed cursor is refused invalid_argument", badCursor.Code == "invalid_argument", badCursor);
    var unpaged = await CallAsync("list_work_items", filter);
    Check("without limit and cursor the same filter returns an array of the labelled items",
        unpaged.Code is null && unpaged.Body.ValueKind == JsonValueKind.Array &&
        unpaged.Body.EnumerateArray().Select(item => StringField(item, "id")!).Order().SequenceEqual(polledIds.Order()),
        unpaged);

    // 14. A comment changes the item's timestamp; nothing changes the timestamp of a sibling that is only displaced.
    var since = StringField(polled[^1], "updatedAtUtc");
    var commented = await CallAsync("add_work_item_comment", new() { ["itemId"] = polledIds[0], ["author"] = "acceptance", ["body"] = $"polling check {run}" });
    Check("add_work_item_comment succeeds", commented.Code is null, commented);
    var reordered = await CallAsync("move_work_item", new() { ["itemId"] = polledIds[2], ["status"] = "Backlog", ["order"] = 0 });
    Check("move_work_item reorders a sibling to the top of its column", reordered.Code is null, reordered);
    var changed = await CallAsync("list_work_items", new(filter) { ["updated_since"] = since });
    var changedIds = changed.Code is null ? changed.Body.EnumerateArray().Select(item => StringField(item, "id")).ToList() : [];
    Check("updated_since taken before the comment returns the commented item and the moved item",
        changedIds.Contains(polledIds[0]) && changedIds.Contains(polledIds[2]), changed);
    Check("updated_since does not return a sibling that was neither edited nor commented on, although the move displaced it",
        changed.Code is null && !changedIds.Contains(polledIds[1]) && !changedIds.Contains(polledIds[3]), changed);
    var sibling = await CallAsync("get_work_item", new() { ["itemId"] = polledIds[1] });
    Check("the displaced sibling keeps its updatedAtUtc",
        sibling.Code is null && unpaged.Code is null &&
        StringField(sibling.Body, "updatedAtUtc") ==
        StringField(unpaged.Body.EnumerateArray().FirstOrDefault(item => StringField(item, "id") == polledIds[1]), "updatedAtUtc"),
        sibling);

    // 15. Comments can be left out.
    var withoutComments = await CallAsync("list_work_items", new(filter) { ["include_comments"] = false });
    Check("with include_comments false no item carries a comment",
        withoutComments.Code is null && withoutComments.Body.GetArrayLength() == polledIds.Count &&
        withoutComments.Body.EnumerateArray().All(item => item.GetProperty("comments").GetArrayLength() == 0) &&
        !withoutComments.Body.GetRawText().Contains($"polling check {run}", StringComparison.Ordinal),
        withoutComments);

    // 16. Claim state follows the clock: a claim that lapses makes the item unclaimed again without any write.
    var held = await CallAsync("claim_work_item", new() { ["id"] = polledIds[3], ["marker"] = markerA, ["ttl_seconds"] = 600 });
    var lapsing = await CallAsync("claim_work_item", new() { ["id"] = polledIds[4], ["marker"] = markerA, ["ttl_seconds"] = 2 });
    Check("two polling items are claimed", held.Code is null && lapsing.Code is null, held.Code is null ? lapsing : held);
    var claimedNow = await IdsAsync(new(filter) { ["claim_state"] = "claimed" });
    var unclaimedNow = await IdsAsync(new(filter) { ["claim_state"] = "unclaimed" });
    Check("claim_state claimed returns the two claimed items, unclaimed the other three",
        claimedNow.SequenceEqual(new[] { polledIds[3], polledIds[4] }.Order()) && unclaimedNow.SequenceEqual(polledIds.Take(3).Order()),
        new ToolOutcome("error", $"claimed {string.Join(", ", claimedNow)}; unclaimed {string.Join(", ", unclaimedNow)}", default));
    await Task.Delay(TimeSpan.FromSeconds(4));
    var claimedLater = await IdsAsync(new(filter) { ["claim_state"] = "claimed" });
    var expiredLater = await IdsAsync(new(filter) { ["claim_state"] = "expired" });
    var (unclaimedLater, _, _) = await WalkAsync(new(filter) { ["claim_state"] = "unclaimed", ["limit"] = 3 });
    Check("once the short claim lapses the item is expired and unclaimed, and the long claim is still claimed",
        claimedLater.SequenceEqual([polledIds[3]]) && expiredLater.SequenceEqual([polledIds[4]]) &&
        unclaimedLater.Order().SequenceEqual(polledIds.Where(id => id != polledIds[3]).Order()),
        new ToolOutcome("error", $"claimed {string.Join(", ", claimedLater)}; expired {string.Join(", ", expiredLater)}; unclaimed {string.Join(", ", unclaimedLater)}", default));

    // 17. A read-scope token can use every new argument.
    if (string.IsNullOrWhiteSpace(readToken))
    {
        Console.WriteLine("SKIP  read-scope use of the list_work_items polling arguments (KANBAN_FACTORY_READ_TOKEN is not set)");
    }
    else
    {
        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        var readPage = await CallAsync("list_work_items", new(filter)
        {
            ["updated_since"] = since, ["claim_state"] = "unclaimed", ["include_comments"] = false, ["limit"] = 1
        }, readClient);
        Check("the read-scope token can list with label, updated_since, claim_state, include_comments and limit",
            readPage.Code is null && readPage.Body.ValueKind == JsonValueKind.Object && readPage.Body.GetProperty("items").GetArrayLength() == 1,
            readPage);
    }
}
finally
{
    foreach (var id in polled.Select(item => StringField(item, "id")).Append(decoyId).Where(id => id is not null))
    {
        var deleted = await CallAsync("delete_work_item", new() { ["itemId"] = id });
        if (deleted.Code is not null)
        {
            Console.Error.WriteLine($"Could not delete polling scratch item {id}: {deleted.Code}: {deleted.Message}");
            failures++;
        }
    }
}

// Links: a pull request URL is attached to an item, found again from the URL, and taken off.
string? linkedId = null;
try
{
    var scratch = await CallAsync("create_work_item", new()
    {
        ["projectId"] = projectId,
        ["title"] = $"Factory acceptance link item {run}",
        ["type"] = "Task"
    });
    if (scratch.Code is not null)
    {
        Console.Error.WriteLine($"Could not create the link scratch item: {scratch.Code}: {scratch.Message}");
        return 1;
    }

    linkedId = StringField(scratch.Body, "id")!;
    var prUrl = $"https://example.com/acceptance/{run}/pull/1";
    Console.WriteLine($"Link scratch item {linkedId}");

    // 18. Add a pr link and read it back on the item.
    var linked = await CallAsync("add_work_item_link", new() { ["id"] = linkedId, ["kind"] = "pr", ["url"] = prUrl });
    var linkId = linked.Code is null ? LinksOf(linked.Body).Select(link => StringField(link, "id")).FirstOrDefault() : null;
    Check("add_work_item_link (kind pr) returns the item with that one link and a link id",
        linked.Code is null && linkId is not null && IsOnlyLink(linked.Body, "pr", prUrl), linked);
    var readLinked = await CallAsync("get_work_item", new() { ["itemId"] = linkedId });
    Check("get_work_item returns the same link",
        readLinked.Code is null && IsOnlyLink(readLinked.Body, "pr", prUrl) &&
        StringField(LinksOf(readLinked.Body)[0], "id") == linkId,
        readLinked);

    // 19. The item is found again from the URL, by that URL only.
    var found = await CallAsync("find_work_items_by_link", new() { ["url"] = prUrl });
    Check("find_work_items_by_link returns exactly the linked item, with its link",
        found.Code is null && found.Body.ValueKind == JsonValueKind.Array && found.Body.GetArrayLength() == 1 &&
        StringField(found.Body[0], "id") == linkedId && IsOnlyLink(found.Body[0], "pr", prUrl),
        found);
    var foundOtherCase = await CallAsync("find_work_items_by_link", new() { ["url"] = prUrl.ToUpperInvariant() });
    Check("find_work_items_by_link with the URL in another case returns an empty list",
        foundOtherCase.Code is null && foundOtherCase.Body.ValueKind == JsonValueKind.Array && foundOtherCase.Body.GetArrayLength() == 0,
        foundOtherCase);

    // 20. Adding the same kind and URL again leaves one link, the same one.
    var linkedAgain = await CallAsync("add_work_item_link", new() { ["id"] = linkedId, ["kind"] = "pr", ["url"] = prUrl });
    Check("adding the same kind and URL again returns the item with still one link, with the same link id",
        linkedAgain.Code is null && IsOnlyLink(linkedAgain.Body, "pr", prUrl) && StringField(LinksOf(linkedAgain.Body)[0], "id") == linkId,
        linkedAgain);
    var badKind = await CallAsync("add_work_item_link", new() { ["id"] = linkedId, ["kind"] = "issue", ["url"] = prUrl });
    Check("add_work_item_link with an unknown kind is refused invalid_argument", badKind.Code == "invalid_argument", badKind);

    // 21. A read-scope token is refused by add and remove, and can find.
    if (string.IsNullOrWhiteSpace(readToken))
    {
        Console.WriteLine("SKIP  read-scope refusal of the link write tools (KANBAN_FACTORY_READ_TOKEN is not set)");
    }
    else
    {
        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        foreach (var tool in new[] { "add_work_item_link", "remove_work_item_link" })
        {
            var refused = await CallAsync(tool, new() { ["id"] = linkedId, ["kind"] = "pr", ["url"] = prUrl }, readClient);
            Check($"{tool} with the read-scope token is refused scope_denied", refused.Code == "scope_denied", refused);
        }

        var readFound = await CallAsync("find_work_items_by_link", new() { ["url"] = prUrl }, readClient);
        Check("the read-scope token can find the item by URL, and the link is still there",
            readFound.Code is null && readFound.Body.GetArrayLength() == 1 && IsOnlyLink(readFound.Body[0], "pr", prUrl),
            readFound);
    }

    // 22. Remove the link; the URL then finds nothing. Removing it again changes nothing.
    var unlinked = await CallAsync("remove_work_item_link", new() { ["id"] = linkedId, ["kind"] = "pr", ["url"] = prUrl });
    Check("remove_work_item_link (kind and url) returns the item without links",
        unlinked.Code is null && LinksOf(unlinked.Body).Length == 0, unlinked);
    var foundAfterRemove = await CallAsync("find_work_items_by_link", new() { ["url"] = prUrl });
    Check("after the removal, find_work_items_by_link returns an empty list",
        foundAfterRemove.Code is null && foundAfterRemove.Body.ValueKind == JsonValueKind.Array && foundAfterRemove.Body.GetArrayLength() == 0,
        foundAfterRemove);
    var unlinkedAgain = await CallAsync("remove_work_item_link", new() { ["id"] = linkedId, ["link_id"] = linkId });
    Check("removing the link again, by link_id, succeeds and changes nothing",
        unlinkedAgain.Code is null && LinksOf(unlinkedAgain.Body).Length == 0, unlinkedAgain);
}
finally
{
    if (linkedId is not null)
    {
        var deleted = await CallAsync("delete_work_item", new() { ["itemId"] = linkedId });
        if (deleted.Code is not null)
        {
            Console.Error.WriteLine($"Could not delete link scratch item {linkedId}: {deleted.Code}: {deleted.Message}");
            failures++;
        }
    }
}

// Comments: a comment is a record with an id, the factory identity as author and a timestamp, and a repeated
// idempotency key adds nothing.
string? commentedId = null;
try
{
    var scratch = await CallAsync("create_work_item", new()
    {
        ["projectId"] = projectId,
        ["title"] = $"Factory acceptance comment item {run}",
        ["type"] = "Task"
    });
    if (scratch.Code is not null)
    {
        Console.Error.WriteLine($"Could not create the comment scratch item: {scratch.Code}: {scratch.Message}");
        return 1;
    }

    commentedId = StringField(scratch.Body, "id")!;
    Console.WriteLine($"Comment scratch item {commentedId}");
    // A claim names the identity behind the token; the comments must carry the same one.
    var identity = await CallAsync("claim_work_item", new() { ["id"] = commentedId, ["marker"] = markerA, ["ttl_seconds"] = 60 });
    var factoryName = StringField(identity.Body, "claimant");
    var factoryId = StringField(identity.Body, "claimant_id");

    // 23. Add a comment with an idempotency key and get its id; the author argument does not name the author.
    var commentKey = $"acceptance-{run}:first";
    var firstComment = await CallAsync("add_work_item_comment", new()
    {
        ["itemId"] = commentedId, ["author"] = "not the factory", ["body"] = $"first {run}", ["idempotency_key"] = commentKey
    });
    var commentId = StringField(firstComment.Body, "comment_id");
    Check("add_work_item_comment with an idempotency key returns comment_id, comment_created true and the item with that comment",
        firstComment.Code is null && commentId is not null && IsFlag(firstComment.Body, "comment_created", true) &&
        CommentsOf(firstComment.Body) is [var onlyComment] && StringField(onlyComment, "id") == commentId,
        firstComment);

    // 24. The same key again, even with another body, returns the first comment and adds nothing.
    var repeatedComment = await CallAsync("add_work_item_comment", new()
    {
        ["itemId"] = commentedId, ["body"] = $"another body {run}", ["idempotency_key"] = commentKey
    });
    Check("a repeated idempotency key returns the same comment_id with comment_created false, still one comment, and the same updatedAtUtc",
        repeatedComment.Code is null && commentId is not null && StringField(repeatedComment.Body, "comment_id") == commentId &&
        IsFlag(repeatedComment.Body, "comment_created", false) &&
        CommentsOf(repeatedComment.Body) is [var keptComment] && StringField(keptComment, "body") == $"first {run}" &&
        StringField(repeatedComment.Body, "updatedAtUtc") == StringField(firstComment.Body, "updatedAtUtc"),
        repeatedComment);
    var badKey = await CallAsync("add_work_item_comment", new() { ["itemId"] = commentedId, ["body"] = "x", ["idempotency_key"] = "two words" });
    Check("add_work_item_comment with a malformed idempotency key is refused invalid_argument", badKey.Code == "invalid_argument", badKey);

    // 25. The comment is listed with the factory identity as author and a timestamp.
    var listedComments = await CallAsync("list_work_item_comments", new() { ["id"] = commentedId });
    Check("list_work_item_comments returns that one comment with the factory identity as author and author_id, a timestamp and its body",
        listedComments.Code is null && factoryName is not null && factoryId is not null &&
        CommentsOf(listedComments.Body, "items") is [var listedComment] &&
        StringField(listedComment, "id") == commentId &&
        StringField(listedComment, "author") == factoryName &&
        StringField(listedComment, "author_id") == factoryId &&
        StringField(listedComment, "body") == $"first {run}" &&
        DateTimeOffset.TryParse(StringField(listedComment, "createdAtUtc"), out _) &&
        IsNullField(listedComments.Body, "next_cursor"),
        listedComments);

    // 26. Several comments are paged through with a small limit, each exactly once and in the order they were written.
    var writtenIds = new List<string?> { commentId };
    for (var number = 2; number <= 5; number++)
    {
        var next = await CallAsync("add_work_item_comment", new() { ["itemId"] = commentedId, ["body"] = $"comment {number} {run}" });
        writtenIds.Add(StringField(next.Body, "comment_id"));
    }

    var pagedIds = new List<string?>();
    var commentPages = 0;
    ToolOutcome? pageFailure = null;
    string? commentCursor = null;
    do
    {
        var arguments = new Dictionary<string, object?> { ["id"] = commentedId, ["limit"] = 2 };
        if (commentCursor is not null)
        {
            arguments["cursor"] = commentCursor;
        }

        var page = await CallAsync("list_work_item_comments", arguments);
        if (page.Code is not null)
        {
            pageFailure = page;
            break;
        }

        commentPages++;
        pagedIds.AddRange(CommentsOf(page.Body, "items").Select(comment => StringField(comment, "id")));
        commentCursor = StringField(page.Body, "next_cursor");
    }
    while (commentCursor is not null && commentPages < 10);
    Check("a list_work_item_comments walk with limit 2 returns the five comments exactly once, in the order they were written, in three pages",
        pageFailure is null && writtenIds.All(id => id is not null) && pagedIds.SequenceEqual(writtenIds) && commentPages == 3,
        pageFailure ?? new ToolOutcome("error", $"walked {string.Join(", ", pagedIds)} in {commentPages} page(s); wrote {string.Join(", ", writtenIds)}", default));
    var badCommentCursor = await CallAsync("list_work_item_comments", new() { ["id"] = commentedId, ["cursor"] = "not-a-cursor" });
    Check("list_work_item_comments with a malformed cursor is refused invalid_argument", badCommentCursor.Code == "invalid_argument", badCommentCursor);
    var unknownItem = await CallAsync("list_work_item_comments", new() { ["id"] = Guid.NewGuid().ToString() });
    Check("list_work_item_comments for an unknown work item is refused not_found", unknownItem.Code == "not_found", unknownItem);

    // 27. A read-scope token can list the comments and is refused by add.
    if (string.IsNullOrWhiteSpace(readToken))
    {
        Console.WriteLine("SKIP  read-scope use of list_work_item_comments (KANBAN_FACTORY_READ_TOKEN is not set)");
    }
    else
    {
        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        var readComments = await CallAsync("list_work_item_comments", new() { ["id"] = commentedId, ["limit"] = 200 }, readClient);
        Check("the read-scope token can list the five comments",
            readComments.Code is null && CommentsOf(readComments.Body, "items").Length == 5, readComments);
        var refusedComment = await CallAsync("add_work_item_comment", new() { ["itemId"] = commentedId, ["body"] = "x", ["idempotency_key"] = commentKey }, readClient);
        Check("add_work_item_comment with the read-scope token is refused scope_denied", refusedComment.Code == "scope_denied", refusedComment);
    }
}
finally
{
    if (commentedId is not null)
    {
        var deleted = await CallAsync("delete_work_item", new() { ["itemId"] = commentedId });
        if (deleted.Code is not null)
        {
            Console.Error.WriteLine($"Could not delete comment scratch item {commentedId}: {deleted.Code}: {deleted.Message}");
            failures++;
        }
    }
}

// Project target and update_project: a scratch project with a repository URL, created under an idempotency key.
string? scratchProjectId = null;
try
{
    const string repositoryUrl = "https://github.com/acme/factory-acceptance.git";
    // 80 random bits: a project key is unique on the board and at most 24 characters.
    var projectRun = Guid.NewGuid().ToString("N")[..20];
    var projectKey = $"ACC{projectRun}".ToUpperInvariant();
    var projectIdempotencyKey = $"acceptance-{projectRun}:project";

    // 28. Create with a repository URL and an idempotency key; a repeat returns the same project.
    var createdProject = await CallAsync("create_project", new()
    {
        ["name"] = $"Factory acceptance scratch project {run}", ["key"] = projectKey, ["repository_url"] = repositoryUrl, ["idempotency_key"] = projectIdempotencyKey
    });
    scratchProjectId = createdProject.Code is null ? StringField(createdProject.Body, "id") : null;
    Check("create_project with a repository URL and an idempotency key returns the project with that URL, no default branch and created true",
        scratchProjectId is not null &&
        StringField(createdProject.Body, "repository_url") == repositoryUrl &&
        HasNullField(createdProject.Body, "default_branch") &&
        IsFlag(createdProject.Body, "created", true),
        createdProject);
    var repeatedProject = await CallAsync("create_project", new()
    {
        ["name"] = "Another name", ["key"] = projectKey, ["repository_url"] = "https://github.com/acme/other.git", ["idempotency_key"] = projectIdempotencyKey
    });
    Check("a repeated idempotency key returns the same project id with created false and the first call's name and URL",
        repeatedProject.Code is null &&
        StringField(repeatedProject.Body, "id") == scratchProjectId &&
        IsFlag(repeatedProject.Body, "created", false) &&
        StringField(repeatedProject.Body, "name") == $"Factory acceptance scratch project {run}" &&
        StringField(repeatedProject.Body, "repository_url") == repositoryUrl,
        repeatedProject);
    var takenKey = await CallAsync("create_project", new() { ["name"] = "Another project", ["key"] = projectKey });
    Check("create_project with the key of another project is refused conflict", takenKey.Code == "conflict", takenKey);
    var badRepository = await CallAsync("create_project", new()
    {
        ["name"] = "Bad", ["key"] = $"BAD{run}".ToUpperInvariant(), ["repository_url"] = "https://user:secret@github.com/acme/app.git"
    });
    Check("create_project with credentials in the repository URL is refused invalid_argument", badRepository.Code == "invalid_argument", badRepository);

    // 29. list_projects, called as before the epic, shows the target of every project.
    var listedProjects = await CallAsync("list_projects", new());
    var listedScratch = ProjectIn(listedProjects, scratchProjectId);
    Check("list_projects without arguments returns the scratch project with its repository_url and a null default_branch",
        listedScratch is { } listed && StringField(listed, "repository_url") == repositoryUrl && HasNullField(listed, "default_branch"),
        listedProjects);
    Check("every listed project carries repository_url and default_branch",
        listedProjects.Code is null && listedProjects.Body.EnumerateArray().All(project =>
            project.TryGetProperty("repository_url", out _) && project.TryGetProperty("default_branch", out _)),
        listedProjects);

    // 30. update_project sets default_branch, clears it, and renames; each call changes only what it names.
    var branched = await CallAsync("update_project", new() { ["id"] = scratchProjectId, ["default_branch"] = "main" });
    Check("update_project sets default_branch and leaves the repository URL, name and key alone",
        branched.Code is null &&
        StringField(branched.Body, "default_branch") == "main" &&
        StringField(branched.Body, "repository_url") == repositoryUrl &&
        StringField(branched.Body, "name") == $"Factory acceptance scratch project {run}" &&
        StringField(branched.Body, "key") == projectKey,
        branched);
    var clearedBranch = await CallAsync("update_project", new() { ["id"] = scratchProjectId, ["clear"] = new[] { "default_branch" } });
    Check("update_project with clear [default_branch] returns a null default_branch and the same repository URL",
        clearedBranch.Code is null && HasNullField(clearedBranch.Body, "default_branch") && StringField(clearedBranch.Body, "repository_url") == repositoryUrl,
        clearedBranch);
    var emptyBranch = await CallAsync("update_project", new() { ["id"] = scratchProjectId, ["default_branch"] = "" });
    Check("update_project with an empty default_branch is refused invalid_argument", emptyBranch.Code == "invalid_argument", emptyBranch);
    var renamedProject = await CallAsync("update_project", new() { ["id"] = scratchProjectId, ["name"] = $"Renamed scratch project {run}" });
    Check("update_project renames the project and leaves the repository URL alone",
        renamedProject.Code is null &&
        StringField(renamedProject.Body, "name") == $"Renamed scratch project {run}" &&
        StringField(renamedProject.Body, "repository_url") == repositoryUrl &&
        HasNullField(renamedProject.Body, "default_branch"),
        renamedProject);
    var unknownProject = await CallAsync("update_project", new() { ["id"] = Guid.NewGuid().ToString(), ["name"] = "x" });
    Check("update_project for an unknown project is refused not_found", unknownProject.Code == "not_found", unknownProject);

    // 31. A read-scope token can list the project and is refused by update_project.
    if (string.IsNullOrWhiteSpace(readToken))
    {
        Console.WriteLine("SKIP  read-scope use of update_project (KANBAN_FACTORY_READ_TOKEN is not set)");
    }
    else
    {
        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        var refusedUpdate = await CallAsync("update_project", new() { ["id"] = scratchProjectId, ["name"] = "refused" }, readClient);
        Check("update_project with the read-scope token is refused scope_denied", refusedUpdate.Code == "scope_denied", refusedUpdate);
        var readProjects = await CallAsync("list_projects", new(), readClient);
        Check("the read-scope token can list the project, and its name is unchanged",
            ProjectIn(readProjects, scratchProjectId) is { } read && StringField(read, "name") == $"Renamed scratch project {run}",
            readProjects);
    }
}
finally
{
    // 32. Archive the scratch project: it leaves list_projects, and the script can run again on this database.
    if (scratchProjectId is not null)
    {
        var archivedProject = await CallAsync("update_project", new() { ["id"] = scratchProjectId, ["is_archived"] = true });
        Check("update_project archives the scratch project", archivedProject.Code is null && IsFlag(archivedProject.Body, "isArchived", true), archivedProject);
        var activeProjects = await CallAsync("list_projects", new());
        var allProjects = await CallAsync("list_projects", new() { ["includeArchived"] = true });
        Check("the archived project is left out of list_projects and returned with includeArchived",
            activeProjects.Code is null && ProjectIn(activeProjects, scratchProjectId) is null && ProjectIn(allProjects, scratchProjectId) is not null,
            allProjects);
        var archivedBoard = await CallAsync("get_board", new() { ["projectId"] = scratchProjectId });
        Check("get_board still answers for the archived project", archivedBoard.Code is null && IsFlag(archivedBoard.Body, "isArchived", true), archivedBoard);
    }
}

// Partial update, expected version, labels as a list and estimate on every read.
string? patchItemId = null;
string? patchSiblingId = null;
try
{
    // 33. A new item has a version, an estimate (null here) and its labels both ways.
    var patchItem = await CallAsync("create_work_item", new()
    {
        ["projectId"] = projectId,
        ["title"] = $"Factory acceptance update item {run}",
        ["type"] = "Task",
        ["description"] = "to be cleared",
        ["estimate"] = 3,
        ["label_list"] = new[] { "acceptance", $"run-{run}" }
    });
    var patchSibling = await CallAsync("create_work_item", new()
    {
        ["projectId"] = projectId,
        ["title"] = $"Factory acceptance update sibling {run}",
        ["type"] = "Task"
    });
    if (patchItem.Code is not null || patchSibling.Code is not null)
    {
        var failed = patchItem.Code is not null ? patchItem : patchSibling;
        Console.Error.WriteLine($"Could not create the update scratch items: {failed.Code}: {failed.Message}");
        return 1;
    }

    patchItemId = StringField(patchItem.Body, "id")!;
    patchSiblingId = StringField(patchSibling.Body, "id")!;
    Console.WriteLine($"Update scratch items {patchItemId}, {patchSiblingId}");
    Check("create_work_item with label_list returns the labels as a list and as the comma-separated string, estimate and version 1",
        LabelsOf(patchItem.Body).SequenceEqual(["acceptance", $"run-{run}"]) &&
        StringField(patchItem.Body, "labels") == $"acceptance,run-{run}" &&
        IntField(patchItem.Body, "estimate") == 3 &&
        IntField(patchItem.Body, "version") == 1,
        patchItem);
    Check("an item created without an estimate carries estimate as null, and an empty label_list",
        HasNullField(patchSibling.Body, "estimate") && LabelsOf(patchSibling.Body).Length == 0 && IntField(patchSibling.Body, "version") == 1,
        patchSibling);
    var readVersion = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId });
    Check("get_work_item returns the item's version", IntField(readVersion.Body, "version") == 1, readVersion);

    // 34. update_work_item with one argument changes that field and leaves the others; the version advances.
    var statusOnly = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["status"] = "Ready" });
    Check("update_work_item with only status changes the status and leaves title, description, estimate, labels, type and priority",
        statusOnly.Code is null &&
        StringField(statusOnly.Body, "status") == "Ready" &&
        StringField(statusOnly.Body, "title") == $"Factory acceptance update item {run}" &&
        StringField(statusOnly.Body, "description") == "to be cleared" &&
        IntField(statusOnly.Body, "estimate") == 3 &&
        LabelsOf(statusOnly.Body).SequenceEqual(["acceptance", $"run-{run}"]) &&
        StringField(statusOnly.Body, "type") == "Task" &&
        StringField(statusOnly.Body, "priority") == "Medium",
        statusOnly);
    Check("the update advanced the version by one", IntField(statusOnly.Body, "version") == 2, statusOnly);
    var titleOnly = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["title"] = $"Renamed update item {run}" });
    Check("update_work_item with only title renames and leaves the status",
        titleOnly.Code is null && StringField(titleOnly.Body, "title") == $"Renamed update item {run}" && StringField(titleOnly.Body, "status") == "Ready" && IntField(titleOnly.Body, "version") == 3,
        titleOnly);

    // 35. The call as it was made before this feature, with all four formerly required arguments, still works.
    var legacy = await CallAsync("update_work_item", new()
    {
        ["itemId"] = patchItemId, ["title"] = $"Renamed update item {run}", ["type"] = "Task", ["status"] = "Ready", ["priority"] = "High"
    });
    Check("update_work_item with title, type, status and priority still works and keeps the optional fields",
        legacy.Code is null && StringField(legacy.Body, "priority") == "High" && StringField(legacy.Body, "description") == "to be cleared" && IntField(legacy.Body, "estimate") == 3,
        legacy);

    // 36. clear empties an optional field.
    var cleared = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["clear"] = new[] { "description", "estimate" } });
    Check("update_work_item with clear [description, estimate] empties both and leaves the labels",
        cleared.Code is null && IsNullField(cleared.Body, "description") && HasNullField(cleared.Body, "estimate") && LabelsOf(cleared.Body).Length == 2,
        cleared);
    // The call as it was made before clear existed, where an empty description or labels emptied the field, still does that.
    await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["description"] = "to be emptied" });
    var emptyDescription = await CallAsync("update_work_item", new()
    {
        ["itemId"] = patchItemId, ["title"] = $"Renamed update item {run}", ["type"] = "Task", ["status"] = "Ready", ["priority"] = "High", ["description"] = ""
    });
    Check("update_work_item with the four old arguments and an empty description empties the description and leaves the labels",
        emptyDescription.Code is null && StringField(emptyDescription.Body, "description") == "" && LabelsOf(emptyDescription.Body).Length == 2 && StringField(emptyDescription.Body, "priority") == "High",
        emptyDescription);
    foreach (var noLabels in new[] { "", " , " })
    {
        await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["labels"] = $"acceptance, run-{run}", ["description"] = "kept" });
        var emptyLabels = await CallAsync("update_work_item", new()
        {
            ["itemId"] = patchItemId, ["title"] = $"Renamed update item {run}", ["type"] = "Task", ["status"] = "Ready", ["priority"] = "High", ["labels"] = noLabels
        });
        Check($"update_work_item with the four old arguments and labels \"{noLabels}\" leaves the item without labels and keeps the description",
            emptyLabels.Code is null && LabelsOf(emptyLabels.Body).Length == 0 && IsNullField(emptyLabels.Body, "labels") && StringField(emptyLabels.Body, "description") == "kept",
            emptyLabels);
    }

    await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["labels"] = $"acceptance, run-{run}", ["clear"] = new[] { "description" } });
    // An update of nothing is refused, and does not advance the version.
    var beforeNothing = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId });
    var nothing = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId });
    var versionOnly = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["expected_version"] = IntField(beforeNothing.Body, "version") });
    var afterNothing = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId });
    Check("update_work_item with only itemId, or only itemId and expected_version, is refused invalid_argument and leaves the version and updatedAtUtc",
        nothing.Code == "invalid_argument" && versionOnly.Code == "invalid_argument" &&
        IntField(beforeNothing.Body, "version") is { } versionBefore && IntField(afterNothing.Body, "version") == versionBefore &&
        StringField(afterNothing.Body, "updatedAtUtc") == StringField(beforeNothing.Body, "updatedAtUtc"),
        nothing);
    var clearTitle = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["clear"] = new[] { "title" } });
    Check("update_work_item with clear [title] is refused invalid_argument", clearTitle.Code == "invalid_argument", clearTitle);

    // 37. More than 240 characters of labels round-trip, and the label filter still finds the item by one of them.
    var manyLabels = Enumerable.Range(0, 60).Select(index => $"acc-{run}-{index:D3}").ToArray();
    var relabelled = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["label_list"] = manyLabels });
    Check($"update_work_item stores {string.Join(',', manyLabels).Length} characters of labels",
        relabelled.Code is null && LabelsOf(relabelled.Body).SequenceEqual(manyLabels) && StringField(relabelled.Body, "labels") == string.Join(',', manyLabels),
        relabelled);
    var rereadLabels = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId });
    Check("get_work_item returns the same labels", LabelsOf(rereadLabels.Body).SequenceEqual(manyLabels), rereadLabels);
    var byLabel = await IdsAsync(new() { ["projectId"] = projectId, ["label"] = manyLabels[^1].ToUpperInvariant() });
    Check("list_work_items with label finds the item by the last of its labels", byLabel.SequenceEqual([patchItemId]), relabelled);
    var commaLabel = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["label_list"] = new[] { "a,b" } });
    Check("a label with a comma in label_list is refused invalid_argument", commaLabel.Code == "invalid_argument", commaLabel);
    var clearedLabels = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["clear"] = new[] { "labels" } });
    Check("update_work_item with clear [labels] leaves the item without labels",
        clearedLabels.Code is null && LabelsOf(clearedLabels.Body).Length == 0 && IsNullField(clearedLabels.Body, "labels"),
        clearedLabels);
    var commaSeparated = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["labels"] = $"acceptance, run-{run}" });
    Check("update_work_item still takes comma-separated labels, which read back as the same labels",
        commaSeparated.Code is null && LabelsOf(commaSeparated.Body).SequenceEqual(["acceptance", $"run-{run}"]),
        commaSeparated);

    // 38. A stale expected_version is refused and changes nothing; the current one succeeds.
    var beforeStale = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId });
    var currentVersion = IntField(beforeStale.Body, "version") ?? -1;
    var staleUpdate = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["status"] = "Done", ["title"] = "stale", ["expected_version"] = currentVersion - 1 });
    Check("update_work_item with a stale expected_version is refused conflict", staleUpdate.Code == "conflict", staleUpdate);
    var afterStale = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId });
    Check("the refused update changed nothing: same version, status, title and updatedAtUtc",
        afterStale.Code is null &&
        IntField(afterStale.Body, "version") == currentVersion &&
        StringField(afterStale.Body, "status") == "Ready" &&
        StringField(afterStale.Body, "title") == $"Renamed update item {run}" &&
        StringField(afterStale.Body, "updatedAtUtc") == StringField(beforeStale.Body, "updatedAtUtc"),
        afterStale);
    var freshUpdate = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["estimate"] = 5, ["expected_version"] = currentVersion });
    Check("update_work_item with the current expected_version succeeds and advances it",
        freshUpdate.Code is null && IntField(freshUpdate.Body, "estimate") == 5 && IntField(freshUpdate.Body, "version") == currentVersion + 1,
        freshUpdate);

    // 39. A claim, a renewal, a comment and a link do not advance the version: the holder's update still succeeds.
    var patchMarker = $"acceptance-{run}-update";
    var held = await CallAsync("claim_work_item", new() { ["id"] = patchItemId, ["marker"] = patchMarker, ["ttl_seconds"] = 60 });
    var heldAgain = await CallAsync("renew_work_item_claim", new() { ["id"] = patchItemId, ["marker"] = patchMarker, ["ttl_seconds"] = 120 });
    var noted = await CallAsync("add_work_item_comment", new() { ["itemId"] = patchItemId, ["body"] = "acceptance note" });
    var prLinked = await CallAsync("add_work_item_link", new() { ["id"] = patchItemId, ["kind"] = "pr", ["url"] = $"https://example.test/acceptance/{run}/pull/7" });
    Check("claim, renew, comment and link leave the version where it was",
        new[] { held, heldAgain, noted, prLinked }.All(outcome => outcome.Code is null && IntField(outcome.Body, "version") == currentVersion + 1),
        prLinked);
    var whileHeld = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["status"] = "InProgress", ["expected_version"] = currentVersion + 1 });
    Check("the claim holder's update with the version it read before renewing succeeds, and keeps the claim, comment and link",
        whileHeld.Code is null &&
        StringField(whileHeld.Body, "status") == "InProgress" &&
        StringField(whileHeld.Body, "claim_marker") == patchMarker &&
        CommentsOf(whileHeld.Body).Length == 1 &&
        LinksOf(whileHeld.Body).Length == 1,
        whileHeld);

    // 40. move_work_item: the current version succeeds, a stale one is refused, and the sibling's version stays.
    var movedCurrent = await CallAsync("move_work_item", new() { ["itemId"] = patchItemId, ["status"] = "Backlog", ["order"] = 0, ["expected_version"] = currentVersion + 2 });
    Check("move_work_item with the current expected_version succeeds and advances it",
        movedCurrent.Code is null && StringField(movedCurrent.Body, "status") == "Backlog" && IntField(movedCurrent.Body, "version") == currentVersion + 3,
        movedCurrent);
    var movedStale = await CallAsync("move_work_item", new() { ["itemId"] = patchItemId, ["status"] = "Done", ["order"] = 0, ["expected_version"] = currentVersion + 2 });
    Check("move_work_item with a stale expected_version is refused conflict", movedStale.Code == "conflict", movedStale);
    var afterStaleMove = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId });
    Check("the refused move left the item in its column at the same version",
        afterStaleMove.Code is null && StringField(afterStaleMove.Body, "status") == "Backlog" && IntField(afterStaleMove.Body, "version") == currentVersion + 3,
        afterStaleMove);
    var siblingAfterMoves = await CallAsync("get_work_item", new() { ["itemId"] = patchSiblingId });
    Check("the sibling the move displaced kept its version and updatedAtUtc",
        siblingAfterMoves.Code is null &&
        IntField(siblingAfterMoves.Body, "version") == 1 &&
        StringField(siblingAfterMoves.Body, "updatedAtUtc") == StringField(patchSibling.Body, "updatedAtUtc"),
        siblingAfterMoves);
    var plainMove = await CallAsync("move_work_item", new() { ["itemId"] = patchItemId, ["status"] = "Ready" });
    Check("move_work_item without expected_version moves unconditionally, as before", plainMove.Code is null && StringField(plainMove.Body, "status") == "Ready", plainMove);

    // 41. estimate and version are on the item from every read: one with an estimate, one without.
    var listedItems = await CallAsync("list_work_items", new() { ["projectId"] = projectId });
    var pagedItems = await CallAsync("list_work_items", new() { ["projectId"] = projectId, ["limit"] = 200, ["include_comments"] = false });
    var boardItems = await CallAsync("get_board", new() { ["projectId"] = projectId });
    var foundItems = await CallAsync("search_work_items", new() { ["query"] = $"update {run}", ["projectId"] = projectId });
    var readItems = new (string Name, JsonElement[] Items)[]
    {
        ("list_work_items", listedItems.Code is null ? listedItems.Body.EnumerateArray().ToArray() : []),
        ("list_work_items with limit", CommentsOf(pagedItems.Body, "items")),
        ("get_board", CommentsOf(boardItems.Body, "items")),
        ("search_work_items", foundItems.Code is null ? foundItems.Body.EnumerateArray().ToArray() : [])
    };
    foreach (var (name, items) in readItems)
    {
        var withEstimate = items.FirstOrDefault(item => StringField(item, "id") == patchItemId);
        var withoutEstimate = items.FirstOrDefault(item => StringField(item, "id") == patchSiblingId);
        Check($"{name} returns estimate 5 and the version for the item, and a null estimate for its sibling",
            IntField(withEstimate, "estimate") == 5 && IntField(withEstimate, "version") == currentVersion + 4 &&
            HasNullField(withoutEstimate, "estimate") && IntField(withoutEstimate, "version") == 1,
            listedItems);
    }

    // 42. An unknown item is refused not_found; a read-scope token is refused by both tools.
    var unknownUpdate = await CallAsync("update_work_item", new() { ["itemId"] = Guid.NewGuid().ToString(), ["status"] = "Done" });
    Check("update_work_item for an unknown item is refused not_found", unknownUpdate.Code == "not_found", unknownUpdate);
    var unknownMove = await CallAsync("move_work_item", new() { ["itemId"] = Guid.NewGuid().ToString(), ["status"] = "Done", ["expected_version"] = 1 });
    Check("move_work_item for an unknown item is refused not_found", unknownMove.Code == "not_found", unknownMove);
    if (string.IsNullOrWhiteSpace(readToken))
    {
        Console.WriteLine("SKIP  read-scope refusal of update_work_item and move_work_item (KANBAN_FACTORY_READ_TOKEN is not set)");
    }
    else
    {
        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        var refusedUpdate = await CallAsync("update_work_item", new() { ["itemId"] = patchItemId, ["status"] = "Done" }, readClient);
        Check("update_work_item with the read-scope token is refused scope_denied", refusedUpdate.Code == "scope_denied", refusedUpdate);
        var refusedMove = await CallAsync("move_work_item", new() { ["itemId"] = patchItemId, ["status"] = "Done" }, readClient);
        Check("move_work_item with the read-scope token is refused scope_denied", refusedMove.Code == "scope_denied", refusedMove);
        var readByReader = await CallAsync("get_work_item", new() { ["itemId"] = patchItemId }, readClient);
        Check("the read-scope token reads the item, unchanged, with its version",
            readByReader.Code is null && StringField(readByReader.Body, "status") == "Ready" && IntField(readByReader.Body, "version") == currentVersion + 4,
            readByReader);
    }
}
finally
{
    foreach (var id in new[] { patchItemId, patchSiblingId }.OfType<string>())
    {
        var deleted = await CallAsync("delete_work_item", new() { ["itemId"] = id });
        if (deleted.Code is not null)
        {
            Console.Error.WriteLine($"Could not delete update scratch item {id}: {deleted.Code}: {deleted.Message}");
            failures++;
        }
    }
}

// Change feed: the cursor is taken first, then an item is claimed, moved, commented on, linked and given a
// dependency, and the feed read from that cursor holds those changes in order, with their content.
// Other callers may write at the same time, so only the entries of this run's own two items are looked at.
string? feedItemId = null;
string? feedBlockerId = null;
try
{
    // 44. Without a cursor list_changes returns no entry and the place to start following from.
    var feedHead = await CallAsync("list_changes", new());
    var headCursor = feedHead.Code is null ? StringField(feedHead.Body, "next_cursor") : null;
    Check("list_changes without a cursor returns no entries and a cursor",
        headCursor is not null && feedHead.Body.TryGetProperty("items", out var headItems) && headItems.GetArrayLength() == 0,
        feedHead);

    var feedItem = await CallAsync("create_work_item", new() { ["projectId"] = projectId, ["title"] = $"Factory acceptance feed item {run}", ["type"] = "Task" });
    var feedBlocker = await CallAsync("create_work_item", new() { ["projectId"] = projectId, ["title"] = $"Factory acceptance feed blocker {run}", ["type"] = "Task" });
    if (headCursor is null || feedItem.Code is not null || feedBlocker.Code is not null)
    {
        var failed = feedItem.Code is not null ? feedItem : feedBlocker.Code is not null ? feedBlocker : feedHead;
        Console.Error.WriteLine($"Could not prepare the change feed checks: {failed.Code}: {failed.Message}");
        failures++;
    }
    else
    {
        feedItemId = StringField(feedItem.Body, "id");
        feedBlockerId = StringField(feedBlocker.Body, "id");
        var feedMarker = $"acceptance-{run}-feed";
        var feedUrl = $"https://example.invalid/acceptance/{run}/pull/1";

        // 45. The writes of the epic's first acceptance tests, in a known order.
        var feedClaim = await CallAsync("claim_work_item", new() { ["id"] = feedItemId, ["marker"] = feedMarker, ["ttl_seconds"] = 60 });
        var feedMove = await CallAsync("move_work_item", new() { ["itemId"] = feedItemId, ["status"] = "InProgress", ["expected_version"] = 1 });
        var feedComment = await CallAsync("add_work_item_comment", new() { ["itemId"] = feedItemId, ["body"] = $"feed comment {run}" });
        var feedLink = await CallAsync("add_work_item_link", new() { ["id"] = feedItemId, ["kind"] = "pr", ["url"] = feedUrl });
        var feedDependency = await CallAsync("add_work_item_dependency", new() { ["id"] = feedItemId, ["blocked_by_id"] = feedBlockerId });
        var feedWrites = new[] { feedClaim, feedMove, feedComment, feedLink, feedDependency };
        Check("the claim, the move, the comment, the link and the dependency all succeed",
            feedWrites.All(write => write.Code is null), feedWrites.FirstOrDefault(write => write.Code is not null) ?? feedClaim);
        var factoryName = feedClaim.Code is null ? StringField(feedClaim.Body, "claimant") : null;
        var factoryId = feedClaim.Code is null ? StringField(feedClaim.Body, "claimant_id") : null;

        // 46. The feed from the cursor taken before them holds them, in order, with their content.
        var (feed, feedEnd, feedFailure) = await ReadFeedAsync(headCursor, feedItemId!, feedBlockerId!);
        Check("list_changes from the saved cursor reads to the end of the feed", feedFailure is null, feedFailure ?? feedHead);
        Check("the feed holds the two creates, the claim, the move, the comment, the link and the dependency, in that order",
            feed.Select(entry => $"{StringField(entry, "kind")}:{StringField(entry, "item_id")}").SequenceEqual(
            [
                $"item_created:{feedItemId}", $"item_created:{feedBlockerId}", $"claimed:{feedItemId}", $"item_moved:{feedItemId}",
                $"comment_added:{feedItemId}", $"link_added:{feedItemId}", $"dependency_added:{feedItemId}"
            ]),
            new ToolOutcome(null, null, JsonSerializer.SerializeToElement(feed)));
        if (feed.Count == 7)
        {
            Check("every entry carries the acceptance project, a later sequence than the one before, a time, and the factory identity as actor",
                feed.All(entry => StringField(entry, "project_id") == projectId && StringField(entry, "created_at_utc") is not null) &&
                feed.Zip(feed.Skip(1)).All(pair => IntField(pair.First, "sequence") < IntField(pair.Second, "sequence")) &&
                factoryId is not null &&
                feed.All(entry => entry.TryGetProperty("actor", out var actor) && StringField(actor, "id") == factoryId && StringField(actor, "name") == factoryName),
                new ToolOutcome(null, null, JsonSerializer.SerializeToElement(feed)));
            Check("the claim entry has the marker, the claimant and the expiry as new values, with null as old",
                ChangeOf(feed[2], "claim_marker") is { } markerChange && IsNullField(markerChange, "old") && StringField(markerChange, "new") == feedMarker &&
                ChangeOf(feed[2], "claimant_id") is { } claimantChange && StringField(claimantChange, "new") == factoryId &&
                ChangeOf(feed[2], "claim_expires_at_utc") is { } expiryChange && StringField(expiryChange, "new") is not null,
                new ToolOutcome(null, null, feed[2]));
            Check("the move entry has the old and the new status and version",
                ChangeOf(feed[3], "status") is { } statusChange && StringField(statusChange, "old") == "Backlog" && StringField(statusChange, "new") == "InProgress" &&
                ChangeOf(feed[3], "version") is { } versionChange && IntField(versionChange, "old") == 1 && IntField(versionChange, "new") == 2,
                new ToolOutcome(null, null, feed[3]));
            Check("the comment entry has the comment's id, author and body",
                ChangeOf(feed[4], "comment") is { } commentChange && IsNullField(commentChange, "old") &&
                commentChange.GetProperty("new") is var comment &&
                StringField(comment, "id") == StringField(feedComment.Body, "comment_id") &&
                StringField(comment, "author") == factoryName &&
                StringField(comment, "body") == $"feed comment {run}",
                new ToolOutcome(null, null, feed[4]));
            Check("the link entry has the link's id, kind and url, and the dependency entry the blocking item",
                ChangeOf(feed[5], "link") is { } linkChange &&
                StringField(linkChange.GetProperty("new"), "id") == StringField(LinksOf(feedLink.Body).FirstOrDefault(), "id") &&
                StringField(linkChange.GetProperty("new"), "kind") == "pr" &&
                StringField(linkChange.GetProperty("new"), "url") == feedUrl &&
                ChangeOf(feed[6], "blocked_by") is { } edgeChange && IsNullField(edgeChange, "old") && StringField(edgeChange, "new") == feedBlockerId,
                new ToolOutcome(null, null, JsonSerializer.SerializeToElement(feed.Skip(5))));
        }

        // 47. Reading again from the same cursor returns the same entries.
        var (again, _, againFailure) = await ReadFeedAsync(headCursor, feedItemId!, feedBlockerId!);
        Check("reading the feed again from the same cursor returns the same entries",
            againFailure is null && again.Select(entry => entry.GetRawText()).SequenceEqual(feed.Select(entry => entry.GetRawText())),
            new ToolOutcome(null, null, JsonSerializer.SerializeToElement(again)));

        // 48. A refused write adds no entry.
        var refusedClaim = await CallAsync("claim_work_item", new() { ["id"] = feedItemId, ["marker"] = $"{feedMarker}-other", ["ttl_seconds"] = 60 });
        Check("a second claim with another marker is refused already_claimed", refusedClaim.Code == "already_claimed", refusedClaim);
        var refusedUpdate = await CallAsync("update_work_item", new() { ["itemId"] = feedItemId, ["title"] = "never written", ["expected_version"] = 1 });
        Check("an update with a stale expected_version is refused conflict", refusedUpdate.Code == "conflict", refusedUpdate);
        var (afterRefusals, afterRefusalsEnd, afterRefusalsFailure) = await ReadFeedAsync(feedEnd!, feedItemId!, feedBlockerId!);
        Check("the refused claim and the refused update added no entry",
            afterRefusalsFailure is null && afterRefusals.Count == 0,
            new ToolOutcome(null, null, JsonSerializer.SerializeToElement(afterRefusals)));

        // 49. A poller that found nothing can go on from the cursor it got back.
        var released = await CallAsync("release_work_item_claim", new() { ["id"] = feedItemId, ["marker"] = feedMarker });
        var (afterRelease, _, afterReleaseFailure) = await ReadFeedAsync(afterRefusalsEnd!, feedItemId!, feedBlockerId!);
        Check("the cursor of an empty read finds the next change: the release, with the marker as old value",
            released.Code is null && afterReleaseFailure is null && afterRelease.Count == 1 &&
            StringField(afterRelease[0], "kind") == "claim_released" &&
            ChangeOf(afterRelease[0], "claim_marker") is { } releasedMarker && StringField(releasedMarker, "old") == feedMarker && IsNullField(releasedMarker, "new"),
            new ToolOutcome(null, null, JsonSerializer.SerializeToElement(afterRelease)));

        // 50. A malformed cursor, and the cursor of another list, are refused.
        var badCursor = await CallAsync("list_changes", new() { ["cursor"] = "not-a-cursor" });
        Check("list_changes with a malformed cursor is refused invalid_argument", badCursor.Code == "invalid_argument", badCursor);
        var itemPage = await CallAsync("list_work_items", new() { ["projectId"] = projectId, ["limit"] = 1 });
        if (itemPage.Code is null && StringField(itemPage.Body, "next_cursor") is { } itemCursor)
        {
            var foreignCursor = await CallAsync("list_changes", new() { ["cursor"] = itemCursor });
            Check("list_changes with a cursor of list_work_items is refused invalid_argument", foreignCursor.Code == "invalid_argument", foreignCursor);
        }

        // 51. The feed is a read tool.
        if (string.IsNullOrWhiteSpace(readToken))
        {
            Console.WriteLine("SKIP  list_changes with the read-scope token (KANBAN_FACTORY_READ_TOKEN is not set)");
        }
        else
        {
            using var readHttpClient = new HttpClient();
            await using var readClient = await ConnectAsync(readHttpClient, readToken);
            var readByReader = await CallAsync("list_changes", new() { ["cursor"] = headCursor, ["project_id"] = projectId, ["limit"] = 200 }, readClient);
            Check("the read-scope token reads the feed",
                readByReader.Code is null && readByReader.Body.TryGetProperty("items", out var readerItems) &&
                readerItems.EnumerateArray().Any(entry => StringField(entry, "item_id") == feedItemId),
                readByReader);
        }
    }
}
finally
{
    foreach (var id in new[] { feedItemId, feedBlockerId }.OfType<string>())
    {
        var deleted = await CallAsync("delete_work_item", new() { ["itemId"] = id });
        if (deleted.Code is not null)
        {
            Console.Error.WriteLine($"Could not delete feed scratch item {id}: {deleted.Code}: {deleted.Message}");
            failures++;
        }
    }
}

if (ownProjectId is not null)
{
    var archivedOwn = await CallAsync("update_project", new() { ["id"] = ownProjectId, ["is_archived"] = true });
    Check("the scratch project of the finer-grained checks is archived", archivedOwn.Code is null, archivedOwn);
}

// The acceptance tests of the epic, literally and in order, in a scratch project of their own.
string? epicProjectId = null;
string? savedCursor = null;
string? planKey = null;
string? epicId = null;
string? factoryIdentityName = null;
string? factoryIdentityId = null;
string? commentIdOnA = null;
var plan = new Dictionary<string, string>();
var planDocuments = new List<string?>();
const string epicRepositoryUrl = "https://github.com/acme/factory-acceptance.git";
var prUrlOnA = $"https://example.com/acceptance/{run}/epic/pull/1";
var epicMarker = $"acceptance-{run}-epic";
var readyFilter = new Dictionary<string, object?> { ["status"] = "Ready", ["unblocked_only"] = true, ["claim_state"] = "unclaimed" };
Dictionary<string, object?> PlanArguments(string? key) => new()
{
    ["project_id"] = epicProjectId,
    ["idempotency_key"] = key,
    ["epic"] = new { name = $"Factory acceptance plan {run}", description = "Imported by the acceptance script." },
    ["documents"] = new[] { new { title = "Requirements", body = $"Requirements of run {run}." }, new { title = "Plan", body = $"Plan of run {run}." } },
    ["items"] = new object[]
    {
        new { @ref = "A", title = $"Plan item A {run}", type = "Task", status = "Ready", priority = "High", estimate = 1, label_list = new[] { "acceptance" } },
        new { @ref = "B", title = $"Plan item B {run}", type = "Task", status = "Ready", blocked_by = new[] { "A" } },
        new { @ref = "C", title = $"Plan item C {run}", type = "Task", status = "Ready", blocked_by = new[] { "B" } }
    }
};
try
{
    await EpicTestAsync(1, "Import a plan of 3 items where C is blocked by B and B by A; list ready, unblocked and unclaimed items and get only A; claim A; a second claim with another marker is refused already_claimed; move A to Done; the list now returns B.", async () =>
    {
        var head = await CallAsync("list_changes", new());
        savedCursor = head.Code is null ? StringField(head.Body, "next_cursor") : null;
        Check("the cursor of the newest entry of the change feed is taken first", savedCursor is not null, head);

        var project = await CallAsync("create_project", new()
        {
            ["name"] = $"Factory acceptance epic tests {run}", ["key"] = $"ACE{Guid.NewGuid().ToString("N")[..20]}".ToUpperInvariant(),
            ["repository_url"] = epicRepositoryUrl, ["default_branch"] = "main", ["idempotency_key"] = $"acceptance-{run}:epic-project"
        });
        epicProjectId = project.Code is null ? StringField(project.Body, "id") : null;
        Check("create_project with a repository URL creates the scratch project",
            epicProjectId is not null && StringField(project.Body, "repository_url") == epicRepositoryUrl && StringField(project.Body, "default_branch") == "main", project);
        Console.WriteLine($"Epic test project {epicProjectId}");

        planKey = $"acceptance-{run}:plan";
        var imported = await CallAsync("import_epic_plan", PlanArguments(planKey));
        epicId = imported.Code is null ? StringField(imported.Body, "epic_id") : null;
        if (imported.Code is null && imported.Body.TryGetProperty("item_ids", out var itemIds) && itemIds.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in itemIds.EnumerateObject())
            {
                plan[entry.Name] = entry.Value.GetString()!;
            }
        }

        planDocuments.AddRange(CommentsOf(imported.Body, "document_ids").Select(id => id.GetString()));
        Check("import_epic_plan returns created true, the epic id, two document ids and an item id for each of A, B and C",
            imported.Code is null && IsFlag(imported.Body, "created", true) && epicId is not null && planDocuments.Count == 2 &&
            plan.Keys.Order().SequenceEqual(["A", "B", "C"]) && plan.Values.Distinct().Count() == 3 &&
            StringField(imported.Body, "project_id") == epicProjectId,
            imported);
        if (plan.Count != 3)
        {
            return true;
        }

        var readA = await CallAsync("get_work_item", new() { ["itemId"] = plan["A"] });
        var readB = await CallAsync("get_work_item", new() { ["itemId"] = plan["B"] });
        var readC = await CallAsync("get_work_item", new() { ["itemId"] = plan["C"] });
        Check("the imported items are Ready, in the epic, at version 1, and carry the dependencies of the plan",
            new[] { readA, readB, readC }.All(read => read.Code is null && StringField(read.Body, "status") == "Ready" && StringField(read.Body, "epicId") == epicId && IntField(read.Body, "version") == 1) &&
            ReferencedIds(readA.Body, "blockers").Length == 0 &&
            ReferencedIds(readB.Body, "blockers").SequenceEqual([plan["A"]]) &&
            ReferencedIds(readC.Body, "blockers").SequenceEqual([plan["B"]]) &&
            StringField(readA.Body, "priority") == "High" && IntField(readA.Body, "estimate") == 1 && LabelsOf(readA.Body).SequenceEqual(["acceptance"]),
            readC);
        var documents = await CallAsync("list_epic_documents", new() { ["epicId"] = epicId });
        Check("the epic has the two documents of the plan, in the order of the plan",
            documents.Code is null && documents.Body.EnumerateArray().Select(document => StringField(document, "id")).SequenceEqual(planDocuments) &&
            documents.Body.EnumerateArray().Select(document => StringField(document, "title")).SequenceEqual(["Requirements", "Plan"]),
            documents);

        var ready = await IdsAsync(new(readyFilter) { ["projectId"] = epicProjectId });
        Check("list_work_items (Ready, unblocked_only, unclaimed) returns only A", ready.SequenceEqual([plan["A"]]),
            new ToolOutcome("error", $"listed {string.Join(", ", ready)}", default));

        var claimedA = await CallAsync("claim_work_item", new() { ["id"] = plan["A"], ["marker"] = epicMarker, ["ttl_seconds"] = 600 });
        factoryIdentityName = StringField(claimedA.Body, "claimant");
        factoryIdentityId = StringField(claimedA.Body, "claimant_id");
        Check("claim_work_item claims A for the factory identity", claimedA.Code is null && StringField(claimedA.Body, "claim_marker") == epicMarker && factoryIdentityId is not null, claimedA);
        var secondClaim = await CallAsync("claim_work_item", new() { ["id"] = plan["A"], ["marker"] = $"{epicMarker}-other", ["ttl_seconds"] = 600 });
        Check("a second claim of A with another marker is refused already_claimed", secondClaim.Code == "already_claimed", secondClaim);

        var movedA = await CallAsync("move_work_item", new() { ["itemId"] = plan["A"], ["status"] = "Done" });
        Check("move_work_item moves A to Done", movedA.Code is null && StringField(movedA.Body, "status") == "Done", movedA);
        var readyAfter = await IdsAsync(new(readyFilter) { ["projectId"] = epicProjectId });
        Check("the same list now returns B, and only B", readyAfter.SequenceEqual([plan["B"]]),
            new ToolOutcome("error", $"listed {string.Join(", ", readyAfter)}", default));
        return true;
    });

    await EpicTestAsync(2, "Repeat the plan import with the same idempotency key: nothing new is created and the same ids come back.", async () =>
    {
        var (before, _, _) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        var repeated = await CallAsync("import_epic_plan", PlanArguments(planKey));
        Check("the repeated import returns created false with the same epic id, document ids and item ids",
            repeated.Code is null && IsFlag(repeated.Body, "created", false) && StringField(repeated.Body, "epic_id") == epicId &&
            CommentsOf(repeated.Body, "document_ids").Select(id => id.GetString()).SequenceEqual(planDocuments) &&
            repeated.Body.TryGetProperty("item_ids", out var repeatedIds) && plan.Count == 3 &&
            repeatedIds.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()).OrderBy(entry => entry.Key)
                .SequenceEqual(plan.OrderBy(entry => entry.Key).Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value))),
            repeated);
        var epics = await CallAsync("list_epics", new() { ["projectId"] = epicProjectId });
        var items = await CallAsync("list_work_items", new() { ["projectId"] = epicProjectId });
        var documents = await CallAsync("list_epic_documents", new() { ["epicId"] = epicId });
        var (after, _, _) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        Check("the project still has one epic, three items and two documents, and its change feed gained no entry",
            epics.Code is null && epics.Body.GetArrayLength() == 1 && items.Code is null && items.Body.GetArrayLength() == 3 &&
            documents.Code is null && documents.Body.GetArrayLength() == 2 && before.Count > 0 && after.Count == before.Count,
            new ToolOutcome("error", $"feed entries before {before.Count}, after {after.Count}", default));
        return true;
    });

    await EpicTestAsync(3, "Attach a PR link to A and find A by that URL; add a comment and read it back with its id and the factory identity as author.", async () =>
    {
        var linked = await CallAsync("add_work_item_link", new() { ["id"] = plan.GetValueOrDefault("A"), ["kind"] = "pr", ["url"] = prUrlOnA });
        Check("add_work_item_link attaches the PR link to A", linked.Code is null && IsOnlyLink(linked.Body, "pr", prUrlOnA), linked);
        var found = await CallAsync("find_work_items_by_link", new() { ["url"] = prUrlOnA });
        Check("find_work_items_by_link with that URL returns exactly A",
            found.Code is null && found.Body.ValueKind == JsonValueKind.Array && found.Body.GetArrayLength() == 1 && StringField(found.Body[0], "id") == plan.GetValueOrDefault("A"),
            found);
        var commented = await CallAsync("add_work_item_comment", new() { ["itemId"] = plan.GetValueOrDefault("A"), ["body"] = $"epic test comment {run}", ["idempotency_key"] = $"acceptance-{run}:epic-comment" });
        commentIdOnA = StringField(commented.Body, "comment_id");
        Check("add_work_item_comment returns the comment's id", commented.Code is null && commentIdOnA is not null, commented);
        var comments = await CallAsync("list_work_item_comments", new() { ["id"] = plan.GetValueOrDefault("A") });
        Check("list_work_item_comments returns the comment with its id, its body and the factory identity as author and author_id",
            comments.Code is null && factoryIdentityId is not null && CommentsOf(comments.Body, "items") is [var comment] &&
            StringField(comment, "id") == commentIdOnA && StringField(comment, "body") == $"epic test comment {run}" &&
            StringField(comment, "author") == factoryIdentityName && StringField(comment, "author_id") == factoryIdentityId,
            comments);
        return true;
    });

    await EpicTestAsync(4, "Read the change feed from the saved cursor: it holds the claim, the move and the comment with their content, in order; read again from the same cursor: same entries.", async () =>
    {
        var (feed, _, feedFailure) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        Check("list_changes from the saved cursor reads to the end of the feed", feedFailure is null, feedFailure ?? new ToolOutcome(null, null, default));
        var kinds = feed.Select(entry => $"{StringField(entry, "kind")}:{plan.FirstOrDefault(item => item.Value == StringField(entry, "item_id")).Key}").ToList();
        Check("the feed of the project holds the import (three items, then two dependencies), the claim, the move, the link and the comment, in that order",
            kinds.SequenceEqual(["item_created:A", "item_created:B", "item_created:C", "dependency_added:B", "dependency_added:C", "claimed:A", "item_moved:A", "link_added:A", "comment_added:A"]),
            new ToolOutcome("error", string.Join(", ", kinds), default));
        Check("every entry has the factory identity as actor and a later sequence than the one before",
            factoryIdentityId is not null &&
            feed.All(entry => entry.TryGetProperty("actor", out var actor) && StringField(actor, "id") == factoryIdentityId && StringField(actor, "name") == factoryIdentityName) &&
            feed.Zip(feed.Skip(1)).All(pair => IntField(pair.First, "sequence") < IntField(pair.Second, "sequence")),
            new ToolOutcome(null, null, JsonSerializer.SerializeToElement(feed)));
        if (feed.Count == 9)
        {
            Check("the import's entries carry the items as they were created and the blocking items",
                ChangeOf(feed[0], "status") is { } createdStatus && IsNullField(createdStatus, "old") && StringField(createdStatus, "new") == "Ready" &&
                ChangeOf(feed[0], "epic_id") is { } createdEpic && StringField(createdEpic, "new") == epicId &&
                ChangeOf(feed[3], "blocked_by") is { } firstEdge && StringField(firstEdge, "new") == plan["A"] &&
                ChangeOf(feed[4], "blocked_by") is { } secondEdge && StringField(secondEdge, "new") == plan["B"],
                new ToolOutcome(null, null, JsonSerializer.SerializeToElement(feed.Take(5))));
            Check("the claim entry has the marker and the claimant, the move entry the old and the new status, the comment entry the comment's id, author and body",
                ChangeOf(feed[5], "claim_marker") is { } markerChange && IsNullField(markerChange, "old") && StringField(markerChange, "new") == epicMarker &&
                ChangeOf(feed[5], "claimant_id") is { } claimantChange && StringField(claimantChange, "new") == factoryIdentityId &&
                ChangeOf(feed[6], "status") is { } statusChange && StringField(statusChange, "old") == "Ready" && StringField(statusChange, "new") == "Done" &&
                ChangeOf(feed[8], "comment") is { } commentChange && commentChange.GetProperty("new") is var comment &&
                StringField(comment, "id") == commentIdOnA && StringField(comment, "author") == factoryIdentityName && StringField(comment, "body") == $"epic test comment {run}",
                new ToolOutcome(null, null, JsonSerializer.SerializeToElement(feed.Skip(5))));
        }

        var (again, _, againFailure) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        Check("reading again from the same cursor returns the same entries",
            againFailure is null && feed.Count > 0 && again.Select(entry => entry.GetRawText()).SequenceEqual(feed.Select(entry => entry.GetRawText())),
            new ToolOutcome(null, null, JsonSerializer.SerializeToElement(again)));
        return true;
    });

    await EpicTestAsync(5, "A short-TTL claim lapses, another marker then claims.", async () =>
    {
        var shortClaim = await CallAsync("claim_work_item", new() { ["id"] = plan.GetValueOrDefault("B"), ["marker"] = $"{epicMarker}-short", ["ttl_seconds"] = 2 });
        Check("claim_work_item claims B for 2 seconds", shortClaim.Code is null && StringField(shortClaim.Body, "claim_marker") == $"{epicMarker}-short", shortClaim);
        var whileHeld = await CallAsync("claim_work_item", new() { ["id"] = plan.GetValueOrDefault("B"), ["marker"] = $"{epicMarker}-next", ["ttl_seconds"] = 600 });
        Check("while the claim is live, another marker is refused already_claimed", whileHeld.Code == "already_claimed", whileHeld);
        await Task.Delay(TimeSpan.FromSeconds(4));
        var lapsed = await CallAsync("get_work_item", new() { ["itemId"] = plan.GetValueOrDefault("B") });
        Check("once it lapses, B reads as unclaimed", lapsed.Code is null && ClaimFields.All(field => IsNullField(lapsed.Body, field)), lapsed);
        var afterLapse = await CallAsync("claim_work_item", new() { ["id"] = plan.GetValueOrDefault("B"), ["marker"] = $"{epicMarker}-next", ["ttl_seconds"] = 600 });
        Check("another marker then claims B", afterLapse.Code is null && StringField(afterLapse.Body, "claim_marker") == $"{epicMarker}-next", afterLapse);
        return true;
    });

    await EpicTestAsync(6, "update_work_item with a stale expected version is refused conflict, and nothing is written.", async () =>
    {
        var read = await CallAsync("get_work_item", new() { ["itemId"] = plan.GetValueOrDefault("B") });
        var version = IntField(read.Body, "version") ?? -1;
        var current = await CallAsync("update_work_item", new() { ["itemId"] = plan.GetValueOrDefault("B"), ["estimate"] = 3, ["expected_version"] = version });
        Check("an update with the version that was read succeeds and advances it", current.Code is null && IntField(current.Body, "version") == version + 1, current);
        var (feedBefore, _, _) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        var stale = await CallAsync("update_work_item", new() { ["itemId"] = plan.GetValueOrDefault("B"), ["title"] = "never written", ["status"] = "Done", ["expected_version"] = version });
        Check("an update with that version again, now stale, is refused conflict", stale.Code == "conflict", stale);
        var after = await CallAsync("get_work_item", new() { ["itemId"] = plan.GetValueOrDefault("B") });
        var (feedAfter, _, _) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        Check("the refused update wrote nothing: same title, status, version and updatedAtUtc, and no entry in the change feed",
            after.Code is null && StringField(after.Body, "title") == $"Plan item B {run}" && StringField(after.Body, "status") == "Ready" &&
            IntField(after.Body, "version") == version + 1 && StringField(after.Body, "updatedAtUtc") == StringField(current.Body, "updatedAtUtc") &&
            feedBefore.Count > 0 && feedAfter.Count == feedBefore.Count,
            after);
        return true;
    });

    await EpicTestAsync(7, "With the read token every read tool succeeds and claim_work_item, import_epic_plan and add_work_item_link are refused scope_denied, nothing written.", async () =>
    {
        if (string.IsNullOrWhiteSpace(readToken))
        {
            Console.WriteLine("SKIP  KANBAN_FACTORY_READ_TOKEN is not set");
            return false;
        }

        using var readHttpClient = new HttpClient();
        await using var readClient = await ConnectAsync(readHttpClient, readToken);
        var reads = new (string Tool, Dictionary<string, object?> Arguments)[]
        {
            ("list_projects", new()),
            ("get_board", new() { ["projectId"] = epicProjectId }),
            ("list_epics", new() { ["projectId"] = epicProjectId }),
            ("get_epic", new() { ["epicId"] = epicId }),
            ("list_epic_documents", new() { ["epicId"] = epicId }),
            ("get_epic_document", new() { ["documentId"] = planDocuments.FirstOrDefault() }),
            ("list_work_items", new(readyFilter) { ["projectId"] = epicProjectId, ["limit"] = 10 }),
            ("get_work_item", new() { ["itemId"] = plan.GetValueOrDefault("A") }),
            ("search_work_items", new() { ["query"] = $"Plan item {run}", ["projectId"] = epicProjectId }),
            ("list_epic_work_items", new() { ["epicId"] = epicId, ["unblocked_only"] = true }),
            ("get_issues", new() { ["projectId"] = epicProjectId }),
            ("find_work_items_by_link", new() { ["url"] = prUrlOnA }),
            ("list_work_item_comments", new() { ["id"] = plan.GetValueOrDefault("A") }),
            ("list_changes", new() { ["cursor"] = savedCursor, ["project_id"] = epicProjectId })
        };
        foreach (var (tool, arguments) in reads)
        {
            var answered = await CallAsync(tool, arguments, readClient);
            Check($"{tool} with the read-scope token succeeds", answered.Code is null, answered);
        }

        var (feedBefore, _, _) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        var itemBefore = await CallAsync("get_work_item", new() { ["itemId"] = plan.GetValueOrDefault("C") });
        var refusedClaim = await CallAsync("claim_work_item", new() { ["id"] = plan.GetValueOrDefault("C"), ["marker"] = $"{epicMarker}-read", ["ttl_seconds"] = 60 }, readClient);
        Check("claim_work_item with the read-scope token is refused scope_denied", refusedClaim.Code == "scope_denied", refusedClaim);
        var refusedImport = await CallAsync("import_epic_plan", PlanArguments($"acceptance-{run}:read-plan"), readClient);
        Check("import_epic_plan with the read-scope token is refused scope_denied", refusedImport.Code == "scope_denied", refusedImport);
        var refusedLink = await CallAsync("add_work_item_link", new() { ["id"] = plan.GetValueOrDefault("C"), ["kind"] = "pr", ["url"] = $"{prUrlOnA}-read" }, readClient);
        Check("add_work_item_link with the read-scope token is refused scope_denied", refusedLink.Code == "scope_denied", refusedLink);

        var itemAfter = await CallAsync("get_work_item", new() { ["itemId"] = plan.GetValueOrDefault("C") });
        var epics = await CallAsync("list_epics", new() { ["projectId"] = epicProjectId });
        var foundRefused = await CallAsync("find_work_items_by_link", new() { ["url"] = $"{prUrlOnA}-read" });
        var (feedAfter, _, _) = await ReadProjectFeedAsync(savedCursor, epicProjectId);
        Check("the refused calls wrote nothing: C is unclaimed and unlinked as before, the project has one epic, and its change feed gained no entry",
            itemBefore.Code is null && itemAfter.Code is null && itemAfter.Body.GetRawText() == itemBefore.Body.GetRawText() &&
            ClaimFields.All(field => IsNullField(itemAfter.Body, field)) && LinksOf(itemAfter.Body).Length == 0 &&
            epics.Code is null && epics.Body.GetArrayLength() == 1 &&
            foundRefused.Code is null && foundRefused.Body.GetArrayLength() == 0 &&
            feedBefore.Count > 0 && feedAfter.Count == feedBefore.Count,
            itemAfter);
        return true;
    });

    await EpicTestAsync(8, "The 11 pre-epic read tools answer calls made with their pre-epic arguments.", async () =>
    {
        // Exactly the arguments each tool took before the epic, all of them passed.
        var calls = new (string Tool, Dictionary<string, object?> Arguments, Func<JsonElement, bool> Answered)[]
        {
            ("list_projects", new() { ["includeArchived"] = true },
                body => body.ValueKind == JsonValueKind.Array && body.EnumerateArray().Any(project => StringField(project, "id") == epicProjectId)),
            ("get_board", new() { ["projectId"] = epicProjectId, ["status"] = "Ready", ["includeArchivedEpics"] = false },
                body => StringField(body, "id") == epicProjectId && CommentsOf(body, "items").Length == 2),
            ("list_epics", new() { ["projectId"] = epicProjectId, ["includeArchived"] = false },
                body => body.ValueKind == JsonValueKind.Array && body.GetArrayLength() == 1 && StringField(body[0], "id") == epicId),
            ("get_epic", new() { ["epicId"] = epicId }, body => StringField(body, "id") == epicId && StringField(body, "projectId") == epicProjectId),
            ("list_epic_documents", new() { ["epicId"] = epicId }, body => body.ValueKind == JsonValueKind.Array && body.GetArrayLength() == 2),
            ("get_epic_document", new() { ["documentId"] = planDocuments.FirstOrDefault() },
                body => StringField(body, "id") == planDocuments.FirstOrDefault() && StringField(body, "title") == "Requirements"),
            ("list_work_items", new() { ["projectId"] = epicProjectId, ["epicId"] = epicId, ["type"] = "Task", ["status"] = "Ready", ["includeArchivedEpics"] = false },
                body => body.ValueKind == JsonValueKind.Array && body.EnumerateArray().Select(item => StringField(item, "id")).Order().SequenceEqual(new[] { plan.GetValueOrDefault("B"), plan.GetValueOrDefault("C") }.Order())),
            ("get_work_item", new() { ["itemId"] = plan.GetValueOrDefault("A") }, body => StringField(body, "id") == plan.GetValueOrDefault("A") && StringField(body, "status") == "Done"),
            ("search_work_items", new() { ["query"] = $"Plan item {run}", ["projectId"] = epicProjectId, ["limit"] = 10 },
                body => body.ValueKind == JsonValueKind.Array && body.GetArrayLength() == 3),
            ("list_epic_work_items", new() { ["epicId"] = epicId, ["type"] = "Task", ["status"] = "Done", ["includeArchivedEpics"] = false },
                body => body.ValueKind == JsonValueKind.Array && body.GetArrayLength() == 1 && StringField(body[0], "id") == plan.GetValueOrDefault("A")),
            ("get_issues", new() { ["projectId"] = epicProjectId, ["status"] = "Ready" }, body => body.ValueKind == JsonValueKind.Array && body.GetArrayLength() == 0)
        };
        var tools = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet();
        foreach (var (tool, arguments, answered) in calls)
        {
            var result = await CallAsync(tool, arguments);
            Check($"{tool}({string.Join(", ", arguments.Keys)}) is still listed and answers", tools.Contains(tool) && result.Code is null && answered(result.Body), result);
        }

        return true;
    });

    // Finer-grained checks of the import and of the keys of the other create tools, in the same scratch project.
    if (epicProjectId is not null && epicId is not null)
    {
        var refusedPlans = new (string What, string Code, object[] Items)[]
        {
            ("a dependency cycle", "cycle", [new { @ref = "X", title = "x", blocked_by = new[] { "Y" } }, new { @ref = "Y", title = "y", blocked_by = new[] { "X" } }]),
            ("a blocked_by that names no item of the plan", "invalid_argument", [new { @ref = "X", title = "x" }, new { @ref = "Y", title = "y", blocked_by = new[] { "Z" } }]),
            ("an item with an unknown status", "invalid_argument", [new { @ref = "X", title = "x" }, new { @ref = "Y", title = "y", status = "Started" }]),
            ("a ref used twice", "invalid_argument", [new { @ref = "X", title = "x" }, new { @ref = "X", title = "y" }])
        };
        foreach (var (what, code, items) in refusedPlans)
        {
            var arguments = PlanArguments($"acceptance-{run}:refused");
            arguments["items"] = items;
            var refused = await CallAsync("import_epic_plan", arguments);
            Check($"import_epic_plan of a plan with {what} is refused {code}", refused.Code == code, refused);
        }

        var missingKey = PlanArguments(null);
        missingKey.Remove("idempotency_key");
        var withoutKey = await CallAsync("import_epic_plan", missingKey);
        Check("import_epic_plan without an idempotency_key is refused invalid_argument", withoutKey.Code == "invalid_argument", withoutKey);
        var unknownProject = PlanArguments($"acceptance-{run}:unknown-project");
        unknownProject["project_id"] = Guid.NewGuid().ToString();
        var inUnknownProject = await CallAsync("import_epic_plan", unknownProject);
        Check("import_epic_plan into an unknown project is refused not_found", inUnknownProject.Code == "not_found", inUnknownProject);
        var epicsAfterRefusals = await CallAsync("list_epics", new() { ["projectId"] = epicProjectId, ["includeArchived"] = true });
        var itemsAfterRefusals = await CallAsync("list_work_items", new() { ["projectId"] = epicProjectId });
        Check("the refused imports created nothing: the project still has one epic and three items",
            epicsAfterRefusals.Code is null && epicsAfterRefusals.Body.GetArrayLength() == 1 && itemsAfterRefusals.Code is null && itemsAfterRefusals.Body.GetArrayLength() == 3,
            epicsAfterRefusals);

        var keyedCreates = new (string Tool, Dictionary<string, object?> Arguments, string NameField)[]
        {
            ("create_epic", new() { ["projectId"] = epicProjectId, ["name"] = $"Keyed epic {run}" }, "name"),
            ("create_epic_document", new() { ["epicId"] = epicId, ["title"] = $"Keyed document {run}", ["body"] = "body" }, "title"),
            ("create_work_item", new() { ["projectId"] = epicProjectId, ["title"] = $"Keyed item {run}", ["type"] = "Task" }, "title"),
            ("create_epic_work_item", new() { ["epicId"] = epicId, ["title"] = $"Keyed epic item {run}" }, "title")
        };
        foreach (var (tool, arguments, nameField) in keyedCreates)
        {
            var key = $"acceptance-{run}:{tool}";
            var first = await CallAsync(tool, new(arguments) { ["idempotency_key"] = key });
            var repeat = await CallAsync(tool, new(arguments) { [nameField] = "another name", ["idempotency_key"] = key });
            Check($"{tool} with an idempotency key says created true, and a repeat returns the same id with created false and the first call's {nameField}",
                first.Code is null && IsFlag(first.Body, "created", true) && StringField(first.Body, "id") is { } firstId &&
                repeat.Code is null && IsFlag(repeat.Body, "created", false) && StringField(repeat.Body, "id") == firstId &&
                StringField(repeat.Body, nameField) == StringField(first.Body, nameField),
                repeat.Code is null ? repeat : first);
            var badKey = await CallAsync(tool, new(arguments) { ["idempotency_key"] = "two words" });
            Check($"{tool} with a malformed idempotency key is refused invalid_argument", badKey.Code == "invalid_argument", badKey);
        }

        var keyedTotals = await CallAsync("list_work_items", new() { ["projectId"] = epicProjectId });
        var keyedEpics = await CallAsync("list_epics", new() { ["projectId"] = epicProjectId });
        var keyedDocuments = await CallAsync("list_epic_documents", new() { ["epicId"] = epicId });
        Check("each keyed create created one object: two epics, three documents and five items in the project",
            keyedEpics.Code is null && keyedEpics.Body.GetArrayLength() == 2 && keyedDocuments.Code is null && keyedDocuments.Body.GetArrayLength() == 3 &&
            keyedTotals.Code is null && keyedTotals.Body.GetArrayLength() == 5,
            keyedTotals);
    }
}
finally
{
    // Leave nothing claimed, and archive the scratch project: it leaves list_projects, and the script can run again.
    if (plan.TryGetValue("A", out var claimedA))
    {
        await CallAsync("release_work_item_claim", new() { ["id"] = claimedA, ["marker"] = epicMarker });
    }

    if (plan.TryGetValue("B", out var claimedB))
    {
        await CallAsync("release_work_item_claim", new() { ["id"] = claimedB, ["marker"] = $"{epicMarker}-next" });
    }

    if (epicProjectId is not null)
    {
        var archivedEpicProject = await CallAsync("update_project", new() { ["id"] = epicProjectId, ["is_archived"] = true });
        Check("the scratch project of the epic acceptance tests is archived", archivedEpicProject.Code is null && IsFlag(archivedEpicProject.Body, "isArchived", true), archivedEpicProject);
    }
}

Console.WriteLine();
Console.WriteLine("EPIC ACCEPTANCE TESTS (sc-25857)");
foreach (var (number, text, result) in epicTests)
{
    Console.WriteLine($"{number}. {result}  {text}");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ACCEPTANCE PASSED" : $"ACCEPTANCE FAILED ({failures} failure(s))");
return failures == 0 ? 0 : 1;

// Runs one acceptance test of the epic and records whether every check in it passed. The test returns false when it
// could not be run at all.
async Task EpicTestAsync(int number, string text, Func<Task<bool>> test)
{
    Console.WriteLine();
    Console.WriteLine($"Epic acceptance test {number}: {text}");
    var failuresBefore = failures;
    bool ran;
    try
    {
        ran = await test();
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        Check($"epic acceptance test {number} ran to its end", false, new ToolOutcome("error", exception.Message, default));
        ran = true;
    }

    epicTests.Add((number, text, !ran ? "SKIP" : failures == failuresBefore ? "PASS" : "FAIL"));
}

// Every entry of one project in the feed from a cursor to its end.
async Task<(List<JsonElement> Entries, string? Cursor, ToolOutcome? Failure)> ReadProjectFeedAsync(string? cursor, string? project)
{
    var entries = new List<JsonElement>();
    for (var pages = 1; pages <= 1000; pages++)
    {
        var page = await CallAsync("list_changes", new() { ["cursor"] = cursor, ["project_id"] = project, ["limit"] = 200 });
        if (cursor is null || project is null || page.Code is not null || page.Body.ValueKind != JsonValueKind.Object ||
            !page.Body.TryGetProperty("items", out var items) || StringField(page.Body, "next_cursor") is not { } next)
        {
            return (entries, null, page.Code is null ? new ToolOutcome("error", "not a page of the feed of the scratch project", default) : page);
        }

        entries.AddRange(items.EnumerateArray());
        cursor = next;
        if (items.GetArrayLength() < 200)
        {
            return (entries, cursor, null);
        }
    }

    return (entries, null, new ToolOutcome("error", "the feed did not end within 1000 pages", default));
}

// Reads the feed of the acceptance project from a cursor to its end and keeps the entries of the given items.
// Returns them with the cursor to go on from.
async Task<(List<JsonElement> Entries, string? Cursor, ToolOutcome? Failure)> ReadFeedAsync(string cursor, params string[] itemIds)
{
    var (entries, next, failure) = await ReadProjectFeedAsync(cursor, projectId);
    return (entries.Where(entry => itemIds.Contains(StringField(entry, "item_id"))).ToList(), next, failure);
}

// Follows next_cursor from the first page to the last; the last page is the one without a next_cursor.
async Task<(List<string> Ids, int Pages, ToolOutcome? Failure)> WalkAsync(Dictionary<string, object?> arguments)
{
    var ids = new List<string>();
    for (var pages = 1; pages <= 100; pages++)
    {
        var page = await CallAsync("list_work_items", arguments);
        if (page.Code is not null || page.Body.ValueKind != JsonValueKind.Object || !page.Body.TryGetProperty("items", out var items))
        {
            return (ids, pages, page.Code is null ? new ToolOutcome("error", $"not a page: {page.Body.GetRawText()}", default) : page);
        }

        ids.AddRange(items.EnumerateArray().Select(item => StringField(item, "id")!));
        if (StringField(page.Body, "next_cursor") is not { } next)
        {
            return (ids, pages, null);
        }

        arguments = new(arguments) { ["cursor"] = next };
    }

    return (ids, 100, new ToolOutcome("error", "the walk did not end within 100 pages", default));
}

// Ids of an unpaged list_work_items result, sorted; a refused call gives the code instead.
async Task<List<string>> IdsAsync(Dictionary<string, object?> arguments)
{
    var listed = await CallAsync("list_work_items", arguments);
    return listed.Code is null
        ? listed.Body.EnumerateArray().Select(item => StringField(item, "id")!).Order().ToList()
        : [$"<{listed.Code}: {listed.Message}>"];
}

Task<ToolOutcome> ClaimAsync(string marker, int ttlSeconds) =>
    CallAsync("claim_work_item", new() { ["id"] = itemId, ["marker"] = marker, ["ttl_seconds"] = ttlSeconds });

void Check(string name, bool passed, ToolOutcome outcome)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}");
    if (!passed)
    {
        failures++;
        Console.WriteLine($"      got: {(outcome.Code is null ? outcome.Body.GetRawText() : $"{outcome.Code}: {outcome.Message}")}");
    }
}

// A refused call is a tool result with isError and structuredContent {code, message}; anything else that fails
// (transport, protocol, an unexpected server fault) is reported under the code "error".
async Task<ToolOutcome> CallAsync(string tool, Dictionary<string, object?> arguments, McpClient? caller = null)
{
    CallToolResult result;
    try
    {
        result = await (caller ?? client).CallToolAsync(tool, arguments);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return new ToolOutcome("error", exception.Message, default);
    }

    var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    if (result.IsError == true)
    {
        return result.StructuredContent is { ValueKind: JsonValueKind.Object } error &&
               error.TryGetProperty("code", out var code)
            ? new ToolOutcome(code.GetString(), error.TryGetProperty("message", out var message) ? message.GetString() : null, default)
            : new ToolOutcome("error", text, default);
    }

    using var document = JsonDocument.Parse(text);
    return new ToolOutcome(null, null, document.RootElement.Clone());
}

Task<McpClient> ConnectAsync(HttpClient http, string bearerToken)
{
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    return McpClient.CreateAsync(new HttpClientTransport(
        new HttpClientTransportOptions { Endpoint = new Uri(mcpUrl) },
        http,
        ownsHttpClient: false));
}

static DateTimeOffset? ExpiryField(JsonElement item) =>
    DateTimeOffset.TryParse(StringField(item, "claim_expires_at_utc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiry)
        ? expiry
        : null;

// Ids in an item's "blockers" or "dependents" array.
static string[] ReferencedIds(JsonElement item, string name) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var references) && references.ValueKind == JsonValueKind.Array
        ? references.EnumerateArray().Select(reference => StringField(reference, "id")!).ToArray()
        : ["<missing>"];

// Which of the scratch items A, B, C a list result contains; other items in the project are ignored.
static string[] ListedOf(JsonElement items, Dictionary<string, string> chain) =>
    chain.Where(entry => items.EnumerateArray().Any(item => StringField(item, "id") == entry.Value))
        .Select(entry => entry.Key)
        .Order()
        .ToArray();

// The entries of an item's "links" array; empty when the field is missing, which no check accepts as a link.
static JsonElement[] LinksOf(JsonElement item) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
        ? links.EnumerateArray().ToArray()
        : [];

static bool IsOnlyLink(JsonElement item, string kind, string url) =>
    LinksOf(item) is [var link] && StringField(link, "kind") == kind && StringField(link, "url") == url;

// The entries of an item's "comments" array, or of a comment page's "items"; empty when the field is missing.
static JsonElement[] CommentsOf(JsonElement container, string name = "comments") =>
    container.ValueKind == JsonValueKind.Object && container.TryGetProperty(name, out var comments) && comments.ValueKind == JsonValueKind.Array
        ? comments.EnumerateArray().ToArray()
        : [];

// The entries of an item's "label_list"; a marker when the field is missing, which no check accepts as labels.
static string[] LabelsOf(JsonElement item) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty("label_list", out var labels) && labels.ValueKind == JsonValueKind.Array
        ? labels.EnumerateArray().Select(label => label.GetString()!).ToArray()
        : ["<missing>"];

static long? IntField(JsonElement item, string name) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        ? value.GetInt64()
        : null;

static bool IsFlag(JsonElement item, string name, bool expected) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
    value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

// The field is there and it is null: a project always carries its repository_url and default_branch.
static bool HasNullField(JsonElement item, string name) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null;

// The project with this id in a list_projects result, or null when it is not listed or the call was refused.
static JsonElement? ProjectIn(ToolOutcome projects, string? id) =>
    projects.Code is null && projects.Body.ValueKind == JsonValueKind.Array &&
    projects.Body.EnumerateArray().Where(project => StringField(project, "id") == id).Select(project => (JsonElement?)project).FirstOrDefault() is { } found
        ? found
        : null;

// The {field, old, new} of an entry of the change feed for one field, or null when the entry does not list it.
static JsonElement? ChangeOf(JsonElement entry, string field) =>
    entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
        ? changes.EnumerateArray().Where(change => StringField(change, "field") == field).Select(change => (JsonElement?)change).FirstOrDefault()
        : null;

static bool IsNullField(JsonElement item, string name) =>
    item.ValueKind == JsonValueKind.Object && (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null);

static string? StringField(JsonElement item, string name) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

internal sealed record ToolOutcome(string? Code, string? Message, JsonElement Body);

internal static partial class Program
{
    private static readonly string[] ClaimFields = ["claimant", "claimant_id", "claim_marker", "claim_expires_at_utc"];
}
