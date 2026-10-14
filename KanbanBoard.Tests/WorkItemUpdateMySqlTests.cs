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
    internal static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EpicId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    // The version is only ever written by the database, as the stored value plus one.
    private const string Advance = @"^UPDATE `WorkItems` AS `w`\nSET `Version` = `w`\.`Version` \+ 1,\n    ";
    private const string HoldItem = "UPDATE `WorkItems` AS `w`\nSET `Version` = `w`.`Version`\nWHERE `w`.`Id` = @itemId";
    // Read outside the transaction, and then the first statement of it: the row of the item's project, held and not changed.
    private const string ReadProject = "SELECT `w`.`ProjectId`\nFROM `WorkItems` AS `w`\nWHERE `w`.`Id` = @itemId\nLIMIT 1";
    internal const string HoldProject = "UPDATE `Projects` AS `p`\nSET `IsArchived` = `p`.`IsArchived`\nWHERE `p`.`Id` = @projectId";
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
        // The entry of the change feed is the last thing the transaction writes. The item was read with its
        // project, so the project is not read again.
        var entry = texts.FindIndex(text => text.StartsWith("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal));
        Assert.Equal(3, WithoutChangeLogEntry(texts.Take(entry + 2).ToList(), readsProject: false).Count);
    }

    [Fact]
    public async Task An_update_without_an_expected_version_advances_the_version_unconditionally()
    {
        var database = new FakeDatabase(lockResult: 1, Answer) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PutAsJsonAsync($"/api/items/{ItemId}", new { status = "Done" });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        // An update that names a status may put the item at the end of another column: it holds the project's row
        // as the first statement of its transaction, before the item's, like every write that places an item.
        Assert.Equal(ReadProject, texts[0]);
        Assert.Equal(HoldProject, texts[1]);
        Assert.Equal(1, database.TransactionStarts[0]);
        Assert.Matches(Advance + @"`UpdatedAtUtc` = @\w+\nWHERE `w`\.`Id` = @itemId$", texts[2]);
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
        Assert.True(texts.Count == 8, string.Join("\n---\n", texts));
        // The move first holds the project and the item and reads the item (see the next test); none of it changes anything.
        Assert.Equal([ReadProject, HoldProject, HoldItem], texts.Skip(2).Take(3));
        Assert.StartsWith("SELECT ", texts[5], StringComparison.Ordinal);
        Assert.All(new[] { texts[1], texts[7] }, text => Assert.Equal("SELECT `w`.`Version`\nFROM `WorkItems` AS `w`\nWHERE `w`.`Id` = @itemId\nLIMIT 1", text));
        Assert.All(new[] { texts[0], texts[6] }, text => Assert.EndsWith("WHERE (`w`.`Id` = @itemId) AND (`w`.`Version` = @expected)", text, StringComparison.Ordinal));
        // A refused write records nothing, renumbers no sibling and commits nothing.
        Assert.DoesNotContain(texts, text => text.Contains("WorkItemChanges", StringComparison.Ordinal) || text.Contains("GET_LOCK(", StringComparison.Ordinal) || text.Contains("`Order` = @", StringComparison.Ordinal) && !text.Contains("`Version` + 1", StringComparison.Ordinal));
        Assert.DoesNotContain("COMMIT", database.Locks);
    }

    [Fact]
    public async Task A_move_holds_the_project_then_the_item_writes_it_in_one_conditional_update_and_renumbers_the_siblings_in_the_same_transaction()
    {
        var database = new FakeDatabase(lockResult: 1, Answer) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PostAsJsonAsync($"/api/items/{ItemId}/move", new { status = "InProgress", order = 0, expectedVersion = 7 });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        // The item's project is read before the transaction, and its row is the first thing the transaction holds:
        // on MariaDB the moves, creates, imports and status changes of one project then run one at a time, and each
        // reads the column after the one before it committed.
        Assert.Equal(ReadProject, texts[0]);
        Assert.Equal(HoldProject, texts[1]);
        // The conditional UPDATE overwrites the column and the position, so the item is held and read before it:
        // the entry of the change feed then reports the values the move replaced.
        Assert.Equal(HoldItem, texts[2]);
        Assert.StartsWith("SELECT ", texts[3], StringComparison.Ordinal);
        Assert.Matches(
            Advance + @"`Status` = @\w+,\n    `Order` = @\w+,\n    `UpdatedAtUtc` = @\w+\nWHERE \(`w`\.`Id` = @itemId\) AND \(`w`\.`Version` = @expected\)$",
            texts[4]);
        // The sibling the fake answers with is renumbered: its order, and nothing else of it.
        var siblings = texts.Where(text => text.StartsWith("UPDATE `WorkItems` SET", StringComparison.Ordinal)).ToList();
        Assert.Matches(@"^UPDATE `WorkItems` SET `Order` = @\w+\nWHERE `Id` = @\w+;", Assert.Single(siblings));
        // One transaction: the renumbering comes before the entry of the change feed, which is the last thing
        // written, and the one commit is made under the lock of the feed.
        var entry = texts.FindIndex(text => text.StartsWith("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal));
        Assert.True(texts.IndexOf(siblings[0]) < entry, string.Join("\n---\n", texts));
        Assert.DoesNotContain(texts.Skip(entry + 1), text => text.StartsWith("UPDATE", StringComparison.Ordinal) || text.StartsWith("INSERT", StringComparison.Ordinal));
        Assert.Equal(1, entry - texts.FindLastIndex(entry, text => text.Contains("GET_LOCK(", StringComparison.Ordinal)));
        Assert.Equal(1, database.TransactionStarts[0]);
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
        Assert.Single(texts, text => text.StartsWith("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Taking_items_out_of_an_epic_holds_them_reads_their_versions_and_then_advances_them_in_one_update()
    {
        var held = new DataTable();
        held.Columns.Add("Id", typeof(string));
        held.Columns.Add("ProjectId", typeof(string));
        held.Columns.Add("Version", typeof(long));
        held.Rows.Add(ItemId.ToString(), ProjectId.ToString(), 4L);
        var database = new FakeDatabase(lockResult: 1, command => command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) ? held : null) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var detached = await WorkItemWrites.DetachFromEpicAsync(dbContext, EpicId, unfinishedOnly: true, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var entry = Assert.Single(detached);
        Assert.Equal((ItemId, ProjectId, WorkItemChangeKinds.ItemUpdated), (entry.WorkItemId, entry.ProjectId, entry.Kind));
        Assert.Equal($$"""[{"field":"epic_id","old":"{{EpicId}}","new":null},{"field":"version","old":4,"new":5}]""", entry.Changes);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        const string where = @"WHERE \(`w`\.`EpicId` = @epicId\) AND `w`\.`Status` NOT IN \(4, 5\)$";
        // The statement that holds the rows comes first: the versions read next are then the ones the update replaces.
        Assert.Matches(@"^UPDATE `WorkItems` AS `w`\nSET `Version` = `w`\.`Version`\n" + where, texts[0]);
        Assert.Matches(@"^SELECT `w`\.`Id`, `w`\.`ProjectId`, `w`\.`Version`\nFROM `WorkItems` AS `w`\n" + where, texts[1]);
        Assert.Matches(
            @"^UPDATE `WorkItems` AS `w`\nSET `EpicId` = NULL,\n    `UpdatedAtUtc` = @\w+,\n    `Version` = `w`\.`Version` \+ 1\n" + where,
            texts[2]);
    }

    [Fact]
    public async Task Search_reads_the_estimate_and_the_version_with_the_fields_it_always_returned()
    {
        var database = new FakeDatabase(lockResult: 1);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await WorkItemSearch.SearchAsync(dbContext, "needle", null, null, new AppOptions(), CancellationToken.None);

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
    internal static DataTable? Answer(DbCommand command)
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
