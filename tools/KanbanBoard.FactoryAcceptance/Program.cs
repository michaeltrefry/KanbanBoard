// Hosted acceptance script for the factory work-source tools (epic sc-25857).
// It talks to a running KanbanBoard MCP server exactly as the orchestrator does: MCP over HTTP with a bearer token.
//
//   KANBAN_MCP_URL                 MCP endpoint, for example https://kanban-mcp.example.net/mcp
//   KANBAN_FACTORY_TOKEN           full-scope token of the factory service identity
//   KANBAN_ACCEPTANCE_PROJECT_ID   project in which the script creates (and afterwards deletes) its scratch work items
//   KANBAN_FACTORY_READ_TOKEN      optional: read-scope token of the same identity; when set, the script also checks
//                                  that the claim, dependency, link, comment and project write tools refuse it with
//                                  scope_denied
//
// The project checks create one scratch project of their own per run (key ACC plus the run id) and leave it archived,
// which keeps it out of list_projects; the script can be run again against the same database.
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
if (string.IsNullOrWhiteSpace(mcpUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(projectId))
{
    Console.Error.WriteLine("Set KANBAN_MCP_URL, KANBAN_FACTORY_TOKEN and KANBAN_ACCEPTANCE_PROJECT_ID.");
    return 2;
}

using var httpClient = new HttpClient();
await using var client = await ConnectAsync(httpClient, token);

var failures = 0;
var run = Guid.NewGuid().ToString("N")[..8];
var markerA = $"acceptance-{run}-a";
var markerB = $"acceptance-{run}-b";

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
    var projectKey = $"ACC{run}".ToUpperInvariant();
    var projectIdempotencyKey = $"acceptance-{run}:project";

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

Console.WriteLine(failures == 0 ? "ACCEPTANCE PASSED" : $"ACCEPTANCE FAILED ({failures} failure(s))");
return failures == 0 ? 0 : 1;

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
