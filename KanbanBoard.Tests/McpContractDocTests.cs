using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KanbanBoard.Mcp.Tools;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol.Client;
using static KanbanBoard.Tests.KanbanToolsClaimTests;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// docs/mcp-contract.md is the contract a caller is built against, so it may not drift from the server. Its tool
// reference is written from the server's own tools/list and compared with it here, character for character; its
// shapes, codes and kinds are checked against the types the server serialises.
//
// To write the tool reference again after a tool, an argument or a description changed:
//   KANBAN_UPDATE_MCP_CONTRACT=1 dotnet test --filter FullyQualifiedName~McpContractDocTests
public sealed class McpContractDocTests
{
    private const string DocPath = "docs/mcp-contract.md";
    private const string Begin = "<!-- BEGIN GENERATED: tool reference (from tools/list; see KanbanBoard.Tests/McpContractDocTests.cs) -->";
    private const string End = "<!-- END GENERATED: tool reference -->";

    [Fact]
    public async Task The_tool_reference_of_the_contract_is_what_tools_list_of_the_server_says()
    {
        await using var server = await McpFixture.CreateAsync(PersonalAccessTokenScopes.Full, _ => Json(HttpStatusCode.OK, "{}"));
        var tools = await server.Client.ListToolsAsync();
        var generated = ToolReference(tools);
        var path = FindRepoFile(DocPath);
        var doc = File.ReadAllText(path).ReplaceLineEndings("\n");
        var begin = doc.IndexOf(Begin, StringComparison.Ordinal);
        var end = doc.IndexOf(End, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, $"{DocPath} has no generated tool reference.");

        if (Environment.GetEnvironmentVariable("KANBAN_UPDATE_MCP_CONTRACT") == "1")
        {
            File.WriteAllText(path, doc[..(begin + Begin.Length)] + "\n\n" + generated + "\n" + doc[end..]);
            return;
        }

        Assert.Equal(generated, doc[(begin + Begin.Length)..end].Trim('\n') + "\n");
        // Nothing the server lists is missing from it, by name: every tool, and every argument under its tool.
        Assert.Equal(37, tools.Count);
        foreach (var tool in tools)
        {
            var section = Section(doc, $"#### `{tool.Name}`", "#### ");
            Assert.All(ArgumentNames(tool), argument => Assert.Contains($"| `{argument}` |", section, StringComparison.Ordinal));
        }
    }

    // The shapes of the contract, by the heading of their table. A table lists every field the server writes, no
    // other, and says for each whether it is always there.
    public static TheoryData<string, Type> Shapes => new()
    {
        { "Project", typeof(ProjectSummaryDto) },
        { "Board", typeof(ProjectBoardDto) },
        { "Epic", typeof(EpicDto) },
        { "Epic document", typeof(EpicDocumentDto) },
        { "Work item", typeof(WorkItemDto) },
        { "Epic reference", typeof(EpicReferenceDto) },
        { "Dependency reference", typeof(WorkItemDependencyRefDto) },
        { "Link", typeof(WorkItemLinkDto) },
        { "Comment", typeof(WorkItemCommentDto) },
        { "Search result", typeof(WorkItemSearchResultDto) },
        { "Work item page", typeof(WorkItemPageDto) },
        { "Comment page", typeof(WorkItemCommentPageDto) },
        { "Change page", typeof(WorkItemChangePageDto) },
        { "Change entry", typeof(WorkItemChangeDto) },
        { "Actor", typeof(WorkItemChangeActorDto) },
        { "Import result", typeof(ImportEpicPlanResultDto) },
        { "Delete result", typeof(KanbanTools.DeleteResultDto) },
        { "Error", typeof(KanbanErrorDto) }
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_shape_of_the_contract_lists_every_field_the_server_writes_and_whether_it_is_always_there(string heading, Type type)
    {
        var doc = File.ReadAllText(FindRepoFile(DocPath)).ReplaceLineEndings("\n");
        var rows = Regex.Matches(Section(doc, $"### {heading}\n", "### "), @"^\| `([^`]+)` \| ([^|]+) \| ([^|]+) \|", RegexOptions.Multiline)
            .ToDictionary(match => match.Groups[1].Value, match => (Type: match.Groups[2].Value.Trim(), Present: match.Groups[3].Value.Trim()));
        var nullability = new NullabilityInfoContext();
        var written = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }.GetTypeInfo(type).Properties
            .Where(property => property.AttributeProvider is PropertyInfo)
            .ToDictionary(property => property.Name, property => (PropertyInfo)property.AttributeProvider!);

        Assert.Equal(written.Keys.Order(), rows.Keys.Order());
        foreach (var (name, property) in written)
        {
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                (!property.PropertyType.IsValueType && nullability.Create(property).ReadState == NullabilityState.Nullable);
            var alwaysWritten = property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Never;
            // The MCP server leaves a null property out unless the property says otherwise.
            var expected = !nullable ? "always" : alwaysWritten ? "always, `null` when" : "left out when";
            Assert.True(rows[name].Present.StartsWith(expected, StringComparison.Ordinal) && (nullable || !rows[name].Present.Contains("null", StringComparison.Ordinal)),
                $"{heading}.{name}: the contract says '{rows[name].Present}', the server writes it '{expected}'.");
            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var expectedType = underlying == typeof(Guid) ? "GUID"
                : underlying == typeof(long) || underlying == typeof(int) ? "integer"
                : underlying == typeof(bool) ? "boolean"
                : underlying == typeof(DateTimeOffset) ? "timestamp"
                : underlying == typeof(string) || underlying.IsEnum ? "string"
                : null;
            Assert.True(expectedType is null || rows[name].Type.StartsWith(expectedType, StringComparison.Ordinal),
                $"{heading}.{name}: the contract says type '{rows[name].Type}', the server writes a {expectedType}.");
        }
    }

    [Fact]
    public void The_contract_lists_every_error_code_change_kind_link_kind_and_enum_value()
    {
        var doc = File.ReadAllText(FindRepoFile(DocPath)).ReplaceLineEndings("\n");
        var codes = typeof(KanbanErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (string)field.GetRawConstantValue()!).ToList();

        Assert.Equal(7, codes.Count);
        // One row per code in the table of codes, and no row for a code the server does not have.
        var codeRows = Regex.Matches(Section(doc, "## Errors\n", "## "), @"^\| `([a-z_]+)` \|", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(codes.Order(), codeRows.Order());
        var kindRows = Regex.Matches(Section(doc, "### Change kinds\n", "### "), @"^\| `([a-z_]+)` \| `", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(WorkItemChangeKinds.All.Order(), kindRows.Order());
        Assert.All(WorkItemLinkKinds.All, kind => Assert.Contains($"`{kind}`", Section(doc, "### Link\n", "### "), StringComparison.Ordinal));
        var enums = Section(doc, "### Enumerations\n", "### ");
        foreach (var value in Enum.GetNames<WorkItemType>().Concat(Enum.GetNames<WorkItemStatus>()).Concat(Enum.GetNames<WorkItemPriority>()))
        {
            Assert.Contains($"`{value}`", enums, StringComparison.Ordinal);
        }
    }

    // The limits the contract states, each against the constant the server enforces.
    [Fact]
    public void The_limits_the_contract_states_are_the_ones_the_server_enforces()
    {
        var limits = Section(File.ReadAllText(FindRepoFile(DocPath)).ReplaceLineEndings("\n"), "### Limits\n", "### ");
        (string Text, int Value)[] stated =
        [
            ("| Items in one plan | 200 |", EpicPlanLimits.MaxItems),
            ("| Documents in one plan | 20 |", EpicPlanLimits.MaxDocuments),
            ("| Dependencies in one plan (all `blocked_by` entries) | 500 |", EpicPlanLimits.MaxDependencies),
            ("| Size of one plan | 2097152 bytes", EpicPlanLimits.MaxRequestBytes),
            ("| `ref` of a plan item | 64 characters |", EpicPlanLimits.MaxRefLength),
            ("| Link `url`, project `repository_url` | 2048 characters |", WorkItemLinkLimits.MaxUrlLength),
            ("| Link `url`, percent-encoded | 6144 characters |", WorkItemLinkLimits.MaxEscapedUrlLength),
            ("| Labels of one item, joined with commas | 4000 characters |", WorkItemLimits.MaxLabelsLength),
            ("| Work item `title` | 240 characters |", WorkItemLimits.MaxTitleLength),
            ("| Epic `name`, project `name` | 160 characters |", EpicLimits.MaxNameLength),
            ("| Epic document `title` | 200 characters |", EpicLimits.MaxDocumentTitleLength),
            ("| Project `key` | 24 characters |", ProjectLimits.MaxKeyLength),
            ("| Project `default_branch` | 255 characters |", ProjectLimits.MaxDefaultBranchLength),
            ("| `idempotency_key` | 128 characters |", IdempotencyKeyLimits.MaxLength),
            ("| Claim `marker` | 200 characters |", WorkItemClaimLimits.MaxMarkerLength),
            ("| Claim `ttl_seconds` | 1 to 86400 |", WorkItemClaimLimits.MaxTtlSeconds),
            ("| One text in a change entry | 16384 characters", KanbanBoard.Api.Services.WorkItemChangeLog.MaxStoredTextLength),
            ("| `search_work_items` `limit` | 100 (default 25) |", KanbanBoard.Api.Services.WorkItemSearch.MaxLimit)
        ];

        Assert.All(stated, limit =>
        {
            Assert.Contains(limit.Text, limits, StringComparison.Ordinal);
            Assert.Contains(limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), limit.Text, StringComparison.Ordinal);
        });
        Assert.Equal(ProjectLimits.MaxNameLength, EpicLimits.MaxNameLength);
        Assert.Equal(ProjectLimits.MaxRepositoryUrlLength, WorkItemLinkLimits.MaxUrlLength);
    }

    // The text from a heading to the next heading of the same level or of a higher one.
    private static string Section(string doc, string heading, string nextHeading)
    {
        var start = doc.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{DocPath} has no section '{heading.Trim()}'.");
        var next = Regex.Match(doc[(start + heading.Length)..], $"^#{{1,{nextHeading.Trim().Length}}} ", RegexOptions.Multiline);
        return next.Success ? doc.Substring(start, heading.Length + next.Index) : doc[start..];
    }

    private static string ToolReference(IList<McpClientTool> listed)
    {
        // By name: the server lists its tools in no particular order.
        var tools = listed.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
        var text = new StringBuilder();
        text.Append("### All tools\n\n");
        text.Append($"The server lists {tools.Count} tools: {tools.Count(tool => !KanbanTools.WriteToolNames.Contains(tool.Name))} read tools and {tools.Count(tool => KanbanTools.WriteToolNames.Contains(tool.Name))} write tools.\n\n");
        text.Append("| Tool | Access | Purpose |\n| --- | --- | --- |\n");
        foreach (var tool in tools)
        {
            text.Append($"| [`{tool.Name}`](#{tool.Name}) | {Access(tool)} | {Cell(Purpose(tool.Description))} |\n");
        }

        text.Append("\n### Arguments of each tool\n");
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.InputSchema;
            var required = schema.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(name => name.GetString()).ToList() : [];
            text.Append($"\n#### `{tool.Name}`\n\n**{(Access(tool) == "write" ? "Write" : "Read")} tool.** {tool.Description}\n\n");
            if (!schema.TryGetProperty("properties", out var properties) || !properties.EnumerateObject().Any())
            {
                text.Append("No arguments.\n");
                continue;
            }

            text.Append("| Argument | Type | Required | Default | Allowed values | Description |\n| --- | --- | --- | --- | --- | --- |\n");
            foreach (var argument in properties.EnumerateObject())
            {
                var isRequired = required.Contains(argument.Name);
                var allowed = argument.Value.TryGetProperty("enum", out var values)
                    ? string.Join(", ", values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => $"`{value.GetString()}`"))
                    : string.Empty;
                var fallback = isRequired || !argument.Value.TryGetProperty("default", out var value) || value.ValueKind == JsonValueKind.Null
                    ? string.Empty
                    : $"`{value.GetRawText()}`";
                var description = argument.Value.TryGetProperty("description", out var described) ? described.GetString()! : string.Empty;
                text.Append($"| `{argument.Name}` | {Cell(TypeOf(argument.Value))} | {(isRequired ? "yes" : "no")} | {fallback} | {allowed} | {Cell(description)} |\n");
            }
        }

        return text.ToString();
    }

    private static string Access(McpClientTool tool) => KanbanTools.WriteToolNames.Contains(tool.Name) ? "write" : "read";

    // The first clause of the description.
    private static string Purpose(string description)
    {
        var end = new[] { ". ", ": ", "; " }.Select(mark => description.IndexOf(mark, StringComparison.Ordinal)).Where(index => index > 0).DefaultIfEmpty(description.Length).Min();
        return description[..end].TrimEnd('.') + ".";
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    // The type of an argument as the schema gives it; null, which the schema allows for every optional argument, is
    // the same as leaving the argument out and is not repeated here.
    private static string TypeOf(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        var name = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(entry => entry.GetString()).First(entry => entry != "null")
            : type.GetString();
        switch (name)
        {
            case "string":
                return schema.TryGetProperty("format", out var format) && format.GetString() == "uuid" ? "string (GUID)" : "string";
            case "array":
                return $"array of {TypeOf(schema.GetProperty("items"))}";
            case "object":
                var required = schema.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(entry => entry.GetString()).ToList() : [];
                return "object {" + string.Join(", ", schema.GetProperty("properties").EnumerateObject()
                    .Select(property => $"{property.Name}{(required.Contains(property.Name) ? string.Empty : "?")}: {TypeOf(property.Value)}")) + "}";
            default:
                return name!;
        }
    }
}
