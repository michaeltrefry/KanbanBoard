using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data.Migrations;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The SQL that MySql.EntityFrameworkCore writes for the update and the move of a work item, which the SQLite tests
// never see. The provider runs for real; FakeDatabase records each command and answers it.
public sealed class WorkItemUpdateMySqlTests
{
    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EpicId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    // The version is only ever written by the database, as the stored value plus one.
    private const string Advance = @"^UPDATE `WorkItems` AS `w`\nSET `Version` = `w`\.`Version` \+ 1,\n    ";
    private static readonly DateTime Created = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_update_with_an_expected_version_is_one_conditional_update_then_the_read_and_the_changed_columns()
    {
        var database = new FakeDatabase(lockResult: 1, Answer) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PatchAsJsonAsync($"/api/items/{ItemId}", new { title = "Renamed", labels = new[] { "api", "two words" }, expectedVersion = 7 });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        // The version is compared and advanced by the database in one statement, which also takes the item's row.
        Assert.Matches(Advance + @"`UpdatedAtUtc` = @\w+\nWHERE \(`w`\.`Id` = @itemId\) AND \(`w`\.`Version` = @expected\)$", texts[0]);
        Assert.StartsWith("SELECT ", texts[1], StringComparison.Ordinal);
        Assert.Contains("FROM `WorkItems` AS `w`", texts[1], StringComparison.Ordinal);
        // Only the columns the update changed are written, and the version is not among them.
        Assert.Matches(@"^UPDATE `WorkItems` SET `Labels` = @\w+, `Title` = @\w+\nWHERE `Id` = @\w+;", texts[2]);
        Assert.DoesNotContain(texts.Skip(1), text => text.StartsWith("UPDATE", StringComparison.Ordinal) && text.Contains("`Version`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_without_an_expected_version_advances_the_version_unconditionally()
    {
        var database = new FakeDatabase(lockResult: 1, Answer) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PutAsJsonAsync($"/api/items/{ItemId}", new { status = "Done" });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.Matches(Advance + @"`UpdatedAtUtc` = @\w+\nWHERE `w`\.`Id` = @itemId$", texts[0]);
        // A new column: the end of it is read (it is empty, so the order stays 0) and the status is written.
        Assert.Contains("SELECT MAX(`w`.`Order`)\nFROM `WorkItems` AS `w`\nWHERE (`w`.`ProjectId` = @projectId) AND (`w`.`Status` = @status)", texts);
        Assert.Contains(texts, text => System.Text.RegularExpressions.Regex.IsMatch(text, @"^UPDATE `WorkItems` SET `Status` = @\w+\nWHERE `Id` = @\w+;"));
    }

    [Fact]
    public async Task An_update_whose_conditional_update_matches_no_row_reads_the_version_and_answers_409_conflict_without_writing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer) { NonQueryResult = 0 };
        using var host = await StartApiAsync(database);

        var update = await host.GetTestClient().PatchAsJsonAsync($"/api/items/{ItemId}", new { title = "Renamed", expectedVersion = 6 });
        var move = await host.GetTestClient().PostAsJsonAsync($"/api/items/{ItemId}/move", new { status = "Done", order = 0, expectedVersion = 6 });

        foreach (var response in new[] { update, move })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, body);
            Assert.Contains("\"code\":\"conflict\"", body, StringComparison.Ordinal);
            Assert.Contains("version 7, not 6", body, StringComparison.Ordinal);
        }

        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 4, string.Join("\n---\n", texts));
        Assert.All(new[] { texts[1], texts[3] }, text => Assert.Equal("SELECT `w`.`Version`\nFROM `WorkItems` AS `w`\nWHERE `w`.`Id` = @itemId\nLIMIT 1", text));
        Assert.All(new[] { texts[0], texts[2] }, text => Assert.EndsWith("WHERE (`w`.`Id` = @itemId) AND (`w`.`Version` = @expected)", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_move_writes_the_item_in_one_conditional_update_and_never_writes_a_siblings_version_or_stamp()
    {
        var database = new FakeDatabase(lockResult: 1, Answer) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PostAsJsonAsync($"/api/items/{ItemId}/move", new { status = "InProgress", order = 0, expectedVersion = 7 });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.Matches(
            Advance + @"`Status` = @\w+,\n    `Order` = @\w+,\n    `UpdatedAtUtc` = @\w+\nWHERE \(`w`\.`Id` = @itemId\) AND \(`w`\.`Version` = @expected\)$",
            texts[0]);
        // The sibling the fake answers with is renumbered: its order, and nothing else of it.
        var siblings = texts.Skip(1).Where(text => text.StartsWith("UPDATE", StringComparison.Ordinal)).ToList();
        Assert.Matches(@"^UPDATE `WorkItems` SET `Order` = @\w+\nWHERE `Id` = @\w+;", Assert.Single(siblings));
    }

    [Fact]
    public async Task Taking_items_out_of_an_epic_is_one_update_that_advances_their_versions()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 2 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var detached = await WorkItemWrites.DetachFromEpicAsync(dbContext, EpicId, unfinishedOnly: true, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(2, detached);
        Assert.Matches(
            @"^UPDATE `WorkItems` AS `w`\nSET `EpicId` = NULL,\n    `UpdatedAtUtc` = @\w+,\n    `Version` = `w`\.`Version` \+ 1\nWHERE \(`w`\.`EpicId` = @epicId\) AND `w`\.`Status` NOT IN \(4, 5\)$",
            Normalize(Assert.Single(database.Commands).Text));
    }

    [Fact]
    public async Task Search_reads_the_estimate_and_the_version_with_the_fields_it_always_returned()
    {
        var database = new FakeDatabase(lockResult: 1);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await WorkItemSearch.SearchAsync(dbContext, "needle", null, null, CancellationToken.None);

        Assert.StartsWith(
            "SELECT `w`.`Id`, `w`.`ProjectId`, `w`.`Title`, `w`.`Type`, `w`.`Status`, `w`.`Estimate`, `w`.`Version`\nFROM `WorkItems` AS `w`",
            Normalize(Assert.Single(database.Commands).Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_and_deploy_script_widen_labels_and_add_the_version_with_one_for_existing_items()
    {
        var migration = new AddWorkItemVersionAndLongerLabels();
        var labels = Assert.Single(migration.UpOperations.OfType<AlterColumnOperation>());
        var version = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
        var script = File.ReadAllText(FindRepoFile("deploy/sql/008_work_item_version_and_longer_labels.sql")).ReplaceLineEndings("\n");

        Assert.Equal(2, migration.UpOperations.Count);
        // The labels column only grows, so the labels already stored are kept.
        Assert.Equal(("WorkItems", "Labels", $"varchar({WorkItemLimits.MaxLabelsLength})", true), (labels.Table, labels.Name, labels.ColumnType, labels.IsNullable));
        Assert.Equal("varchar(240)", labels.OldColumn.ColumnType);
        Assert.Contains($"ALTER TABLE `WorkItems` MODIFY COLUMN `Labels` {labels.ColumnType} NULL;", script, StringComparison.Ordinal);
        // Every existing item gets version 1, like a new item.
        Assert.Equal(("WorkItems", "Version", "bigint", false, 1L), (version.Table, version.Name, version.ColumnType, version.IsNullable, version.DefaultValue));
        Assert.Contains("ALTER TABLE `WorkItems` ADD COLUMN IF NOT EXISTS `Version` bigint NOT NULL DEFAULT 1;", script, StringComparison.Ordinal);
        Assert.Contains("'20261013004410_AddWorkItemVersionAndLongerLabels'", script, StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n").TrimEnd();

    // The item at version 7 in Backlog without an epic, and one sibling in every column; nothing else has rows.
    private static DataTable? Answer(DbCommand command)
    {
        var text = Normalize(command.CommandText);
        if (text.Contains("ROW_COUNT()", StringComparison.Ordinal))
        {
            return Table("ROW_COUNT()", 1);
        }

        // The end of an empty column.
        if (text.StartsWith("SELECT MAX(", StringComparison.Ordinal))
        {
            return Table("m", DBNull.Value);
        }

        if (!text.StartsWith("SELECT ", StringComparison.Ordinal) || !text.Contains("FROM `WorkItems` AS `w`", StringComparison.Ordinal) ||
            text.Contains("WorkItemDependencies", StringComparison.Ordinal) || text.Contains("WorkItemLinks", StringComparison.Ordinal))
        {
            return null;
        }

        // A sibling query excludes the moved item; every other query reads the item itself.
        var sibling = text.Contains("`w`.`Id` <> @", StringComparison.Ordinal);
        var values = new Dictionary<string, object>
        {
            ["Id"] = sibling ? Guid.Parse("99999999-9999-9999-9999-999999999999").ToString() : ItemId.ToString(),
            ["ProjectId"] = Guid.Empty.ToString(),
            ["Title"] = "Item",
            ["Type"] = 0,
            ["Status"] = 0,
            ["Priority"] = 1,
            ["Order"] = 0,
            ["CreatedAtUtc"] = Created,
            ["UpdatedAtUtc"] = Created,
            ["Version"] = 7L
        };
        var table = new DataTable();
        var columns = text["SELECT ".Length..text.IndexOf('\n', StringComparison.Ordinal)].Split(", ");
        var alias = columns[0][..columns[0].IndexOf('.', StringComparison.Ordinal)];
        foreach (var column in columns)
        {
            table.Columns.Add(column, typeof(object));
        }

        table.Rows.Add(columns
            .Select(column => column.StartsWith(alias + ".", StringComparison.Ordinal)
                ? values.GetValueOrDefault(column[(alias.Length + 1)..].Trim('`'), DBNull.Value)
                : DBNull.Value)
            .ToArray());
        return table;
    }

    private static DataTable Table(string name, object value)
    {
        var table = new DataTable();
        table.Columns.Add(name, value is DBNull ? typeof(int) : value.GetType());
        table.Rows.Add(value);
        return table;
    }

    private static Task<IHost> StartApiAsync(FakeDatabase database) =>
        new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddScoped(_ => CreatePipelineContext(new DatabaseOptions(), database));
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<BoardChangeNotifier>();
                    services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapKanbanWorkItemWriteEndpoints());
                });
            })
            .StartAsync();
}
