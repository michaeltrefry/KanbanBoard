using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data.Migrations;
using KanbanBoard.Api.Models;
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

// The SQL that MySql.EntityFrameworkCore writes for the change feed, which the SQLite tests never see: the read of
// the feed, the statements that record an entry, and the statements a claim runs around its conditional UPDATE.
// The provider runs for real; FakeDatabase records each command and answers it.
public sealed class WorkItemChangeFeedMySqlTests
{
    private const string HoldItem = "UPDATE `WorkItems` AS `w`\nSET `Version` = `w`.`Version`\nWHERE `w`.`Id` = @itemId";
    private static readonly Guid ItemId = WorkItemUpdateMySqlTests.ItemId;
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_feed_is_read_as_a_range_of_sequences_with_a_limit_and_the_place_to_start_from_as_one_max()
    {
        var database = new FakeDatabase(lockResult: 1, command => command.CommandText.Contains("MAX(", StringComparison.Ordinal) ? One("m", 41L) : null);
        using var host = await StartApiAsync(database);
        var client = host.GetTestClient();

        var head = await client.GetFromJsonAsync<WorkItemChangePageDto>("/api/items/changes");
        var page = await client.GetFromJsonAsync<WorkItemChangePageDto>($"/api/items/changes?cursor={head!.NextCursor}&limit=7");
        var ofProject = await client.GetFromJsonAsync<WorkItemChangePageDto>($"/api/items/changes?cursor={head.NextCursor}&projectId={ProjectId}");

        Assert.Equal(WorkItemChangeLog.EncodeCursor(41), head.NextCursor);
        // Nothing came back, so the reader keeps its place.
        Assert.Equal((head.NextCursor, head.NextCursor), (page!.NextCursor, ofProject!.NextCursor));
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 5, string.Join("\n---\n", texts));
        const string max = "SELECT MAX(`w`.`Sequence`)\nFROM `WorkItemChanges` AS `w`";
        Assert.Equal(max, texts[0]);
        const string columns = "SELECT `w`.`Sequence`, `w`.`ActorName`, `w`.`ActorUserId`, `w`.`Changes`, `w`.`CreatedAtUtc`, `w`.`Kind`, `w`.`ProjectId`, `w`.`WorkItemId`\nFROM `WorkItemChanges` AS `w`\n";
        // The primary key: a range and a limit, never an offset.
        Assert.Equal(columns + "WHERE `w`.`Sequence` > @after\nORDER BY `w`.`Sequence`\nLIMIT @p", texts[1]);
        // IX_WorkItemChanges_ProjectId_Sequence.
        Assert.Equal(columns + "WHERE (`w`.`Sequence` > @after) AND (`w`.`ProjectId` = @projectId)\nORDER BY `w`.`Sequence`\nLIMIT @p", texts[3]);
        // A read that returned nothing asks once more where the feed ends: its cursor must not be ahead of that.
        Assert.Equal((max, max), (texts[2], texts[4]));
        Assert.All(database.Commands.Skip(1), command => Assert.DoesNotContain("OFFSET", command.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_is_inserted_and_committed_under_the_named_lock_which_is_released_only_after_the_commit()
    {
        var database = new FakeDatabase(lockResult: 1);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var entry = WorkItemChangeLog.Entry(
            ItemId, ProjectId, WorkItemChangeKinds.ItemUpdated, new ChangeActor(ProjectId, "Factory"), Now,
            new Dictionary<string, object?> { ["status"] = WorkItemStatus.Backlog, ["estimate"] = null, ["title"] = "Same", ["description"] = null, ["at"] = null },
            new Dictionary<string, object?> { ["status"] = WorkItemStatus.Done, ["estimate"] = 3, ["title"] = "Same", ["description"] = "\"é\" + <日本>", ["at"] = Now });

        await WorkItemChangeLog.CommitAsync(dbContext, transaction, entry, CancellationToken.None);

        // A field that kept its value is not listed, and text is stored as it is: only what JSON requires is escaped.
        Assert.Equal(
            """[{"field":"status","old":"Backlog","new":"Done"},{"field":"estimate","old":null,"new":3},{"field":"description","old":null,"new":"\"é\" + <日本>"},{"field":"at","old":null,"new":"2026-10-12T12:00:00+00:00"}]""",
            entry.Changes);
        Assert.Equal(1L, entry.Sequence);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.Empty(WithoutChangeLogEntry(texts, readsProject: false));
        Assert.Equal("SELECT CAST(GET_LOCK(@p0, @p1) AS SIGNED) AS `Value`", texts[0]);
        // The database numbers the entry: the sequence is not sent, it is read back.
        Assert.EndsWith("SELECT `Sequence`\nFROM `WorkItemChanges`\nWHERE ROW_COUNT() = 1 AND `Sequence` = LAST_INSERT_ID();", texts[1], StringComparison.Ordinal);
        // The commit comes before the release: a later writer cannot be numbered, and seen, before this one is stored.
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Fact]
    public async Task A_write_with_no_entry_commits_without_the_named_lock()
    {
        var database = new FakeDatabase(lockResult: 1);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        await WorkItemChangeLog.CommitAsync(dbContext, transaction, Array.Empty<WorkItemChange>(), CancellationToken.None);

        Assert.Empty(database.Commands);
        Assert.Equal(["COMMIT"], database.Locks);
    }

    [Fact]
    public async Task A_write_that_does_not_get_the_named_lock_is_refused_busy_and_neither_records_nor_commits()
    {
        var database = new FakeDatabase(lockResult: 1, command =>
            command.CommandText.Contains("GET_LOCK(", StringComparison.Ordinal) ? One("Value", 0L) : WorkItemUpdateMySqlTests.Answer(command)) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        using var response = await host.GetTestClient().PatchAsJsonAsync($"/api/items/{ItemId}", new { title = "Renamed" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
        Assert.Equal(KanbanErrorCodes.Busy, error!.Code);
        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(database.Commands, command => command.Text.Contains("WorkItemChanges", StringComparison.Ordinal));
        // The update of the item was sent; its transaction is not committed after the lock was refused.
        Assert.Equal($"RELEASE {WorkItemChangeLog.LockName}", database.Locks[^1]);
        Assert.Equal($"GET {WorkItemChangeLog.LockName}", database.Locks[^2]);
    }

    [Fact]
    public async Task A_claim_holds_the_item_and_reads_the_stored_claim_before_its_conditional_update_and_records_the_entry_last()
    {
        var database = new FakeDatabase(lockResult: 1, WorkItemUpdateMySqlTests.Answer) { NonQueryResult = 1 };
        // A context for each call, as for each request: the fake gives every entry the same sequence.
        await using var claimContext = CreatePipelineContext(new DatabaseOptions(), database);
        await using var renewContext = CreatePipelineContext(new DatabaseOptions(), database);
        await using var releaseContext = CreatePipelineContext(new DatabaseOptions(), database);
        var claimant = new AppUser { DisplayName = "Factory" };

        var claimed = await WorkItemClaims.ClaimAsync(claimContext, ItemId, claimant, "run-1", TimeSpan.FromMinutes(1), Now, CancellationToken.None);
        var first = database.Commands.Count;
        var renewed = await WorkItemClaims.RenewAsync(renewContext, ItemId, claimant, "run-1", TimeSpan.FromMinutes(1), Now, CancellationToken.None);
        var second = database.Commands.Count;
        var released = await WorkItemClaims.ReleaseAsync(releaseContext, ItemId, claimant, "run-1", Now, CancellationToken.None);

        Assert.Equal(
            [WorkItemClaimOutcome.Succeeded, WorkItemClaimOutcome.Succeeded, WorkItemClaimOutcome.Succeeded],
            new[] { claimed.Outcome, renewed.Outcome, released.Outcome });
        var all = database.Commands.Select(command => Normalize(command.Text)).ToList();
        // What only the conditional UPDATE of each write has in its WHERE.
        string[] conditions = ["`w`.`ClaimExpiresAtUtc` <= @now", "`w`.`ClaimExpiresAtUtc` > @now", "`w`.`ClaimMarker` = @marker"];
        var writes = new[] { all.Take(first), all.Skip(first).Take(second - first), all.Skip(second) }.Select(texts => texts.ToList()).ToList();
        for (var index = 0; index < writes.Count; index++)
        {
            var texts = WithoutChangeLogEntry(writes[index], readsProject: false);
            Assert.True(texts.Count == 4, string.Join("\n---\n", texts));
            // The statement that holds the row comes first, so the read after it returns the claim the UPDATE replaces.
            Assert.Equal(HoldItem, texts[0]);
            Assert.StartsWith("SELECT ", texts[1], StringComparison.Ordinal);
            // The database still decides the winner, in one conditional UPDATE.
            Assert.StartsWith("UPDATE `WorkItems` AS `w`\nSET `Claim", texts[2], StringComparison.Ordinal);
            Assert.Contains(conditions[index], texts[2][texts[2].IndexOf("\nWHERE ", StringComparison.Ordinal)..], StringComparison.Ordinal);
            Assert.StartsWith("SELECT ", texts[3], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_claim_whose_conditional_update_matches_no_row_is_refused_and_records_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, WorkItemUpdateMySqlTests.Answer) { NonQueryResult = 0 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await WorkItemClaims.ClaimAsync(dbContext, ItemId, new AppUser { DisplayName = "Factory" }, "run-1", TimeSpan.FromMinutes(1), Now, CancellationToken.None);

        Assert.Equal(WorkItemClaimOutcome.AlreadyClaimed, result.Outcome);
        Assert.Equal(3, database.Commands.Count);
        Assert.Empty(database.Locks);
        Assert.DoesNotContain(database.Commands, command => command.Text.Contains("WorkItemChanges", StringComparison.Ordinal));
    }

    [Fact]
    public void Migration_and_deploy_script_add_the_table_with_a_sequence_the_database_assigns_and_no_foreign_key()
    {
        var migration = new AddWorkItemChanges();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        var script = File.ReadAllText(FindRepoFile("deploy/sql/009_work_item_changes.sql")).ReplaceLineEndings("\n");

        Assert.Equal(2, migration.UpOperations.Count);
        Assert.Equal("WorkItemChanges", table.Name);
        // An entry outlives its item and its project.
        Assert.Empty(table.ForeignKeys);
        Assert.Equal(["Sequence"], table.PrimaryKey!.Columns);
        Assert.Equal(
            ["Sequence bigint", "WorkItemId varchar(36)", "ProjectId varchar(36)", "Kind varchar(32)", "ActorUserId varchar(36)", "ActorName varchar(320)", "Changes longtext", "CreatedAtUtc datetime(6)"],
            table.Columns.Select(column => $"{column.Name} {column.ColumnType}").ToArray());
        Assert.Equal(["ActorUserId", "ActorName"], table.Columns.Where(column => column.IsNullable).Select(column => column.Name).ToArray());
        Assert.Equal(("IX_WorkItemChanges_ProjectId_Sequence", false), (index.Name, index.IsUnique));
        Assert.Equal(["ProjectId", "Sequence"], index.Columns);
        foreach (var column in table.Columns)
        {
            Assert.Contains(
                $"    `{column.Name}` {column.ColumnType} {(column.IsNullable ? "NULL" : "NOT NULL")}{(column.Name == "Sequence" ? " AUTO_INCREMENT" : string.Empty)},\n",
                script, StringComparison.Ordinal);
        }

        Assert.Contains("CREATE TABLE IF NOT EXISTS `WorkItemChanges` (", script, StringComparison.Ordinal);
        Assert.Contains("    PRIMARY KEY (`Sequence`),\n    KEY `IX_WorkItemChanges_ProjectId_Sequence` (`ProjectId`, `Sequence`)\n);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN KEY", script, StringComparison.Ordinal);
        Assert.Contains("'20261013013744_AddWorkItemChanges'", script, StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n").TrimEnd();

    private static DataTable One(string name, object value)
    {
        var table = new DataTable();
        table.Columns.Add(name, value.GetType());
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
                });
                webBuilder.Configure(app =>
                {
                    // As in Program.cs.
                    app.UseKanbanBusyRefusal();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapKanbanChangeFeedEndpoints();
                        endpoints.MapKanbanWorkItemWriteEndpoints();
                    });
                });
            })
            .StartAsync();
}
