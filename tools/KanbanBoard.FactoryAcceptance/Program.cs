// Hosted acceptance script for the factory work-source tools (epic sc-25857).
// It talks to a running KanbanBoard MCP server exactly as the orchestrator does: MCP over HTTP with a bearer token.
//
//   KANBAN_MCP_URL                 MCP endpoint, for example https://kanban-mcp.example.net/mcp
//   KANBAN_FACTORY_TOKEN           full-scope token of the factory service identity
//   KANBAN_ACCEPTANCE_PROJECT_ID   project in which the script creates (and afterwards deletes) one scratch work item
//   KANBAN_FACTORY_READ_TOKEN      optional: read-scope token of the same identity; when set, the script also checks
//                                  that the three claim tools refuse it with scope_denied
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

Console.WriteLine(failures == 0 ? "ACCEPTANCE PASSED" : $"ACCEPTANCE FAILED ({failures} failure(s))");
return failures == 0 ? 0 : 1;

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
