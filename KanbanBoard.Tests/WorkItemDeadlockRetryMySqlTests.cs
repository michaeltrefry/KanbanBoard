using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The writes that hold the rows of several items at once (deleting a project, archiving or deleting an epic), on
// the real application (Program.cs) with the SQL MySql.EntityFrameworkCore writes: MariaDB can pick them as the
// victim of a deadlock (error 1213), which SQLite never does. So can a write to one item that rewrites the item's
// index entries while it holds the item's row (a move, an update, a delete), when it meets one of those.
// FakeDatabase records each command and answers it.
public sealed class WorkItemDeadlockRetryMySqlTests
{
    private const string HoldItems = "SET `Version` = `w`.`Version`";
    private const string HoldBlockedByProject =
        "UPDATE `WorkItems` AS `i` STRAIGHT_JOIN `WorkItemDependencies` AS `d` ON `d`.`BlockerItemId` = `i`.`Id` SET `d`.`BlockerItemId` = `d`.`BlockerItemId` WHERE `i`.`ProjectId` = @p0";
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EpicId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid BlockedItemId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtherProjectId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTime Created = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string, string, bool, int> Writes => new()
    {
        // Deleting the project: one entry for its item and one for the item of another project that item blocked.
        { "DELETE", $"/api/projects/{ProjectId}", false, 2 },
        { "DELETE", $"/api/epics/{EpicId}", false, 1 },
        // Archiving the epic.
        { "PUT", $"/api/epics/{EpicId}", true, 1 }
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_that_was_the_victim_of_one_deadlock_is_run_again_and_records_its_entries_once(string method, string path, bool hasBody, int entries)
    {
        var deadlocks = 1;
        var database = Database(() => deadlocks-- > 0);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, method, path, hasBody);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();
        // The statement that holds the items was sent twice: the attempt that deadlocked, and the one that went through.
        Assert.Equal(2, texts.Count(text => text.Contains(HoldItems, StringComparison.Ordinal)));
        var insert = Assert.Single(texts, text => text.Contains("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal));
        Assert.Equal(entries, insert.Split("INSERT INTO `WorkItemChanges`").Length - 1);
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_that_deadlocks_three_times_is_refused_busy_and_stores_nothing(string method, string path, bool hasBody, int entries)
    {
        var database = Database(() => true);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, method, path, hasBody);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
        Assert.Equal(KanbanErrorCodes.Busy, error!.Code);
        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        var texts = database.Commands.Select(command => command.Text).ToList();
        // The first attempt and two more.
        Assert.Equal(3, texts.Count(text => text.Contains(HoldItems, StringComparison.Ordinal)));
        Assert.True(entries > 0);
        // The statement that holds the epic's row (UPDATE `Epics` AS `e`, which changes nothing) is not the save of the epic.
        Assert.DoesNotContain(texts, text =>
            text.Contains("INSERT INTO", StringComparison.Ordinal) || text.Contains("DELETE FROM", StringComparison.Ordinal) || text.Contains("UPDATE `Epics` SET", StringComparison.Ordinal));
        Assert.DoesNotContain("COMMIT", database.Locks);
    }

    private const string HoldOneItem = "SET `Version` = `w`.`Version`\nWHERE `w`.`Id` = @itemId";
    private const string AdvanceVersion = "SET `Version` = `w`.`Version` + 1";

    // A write to one item, the statement of it that deadlocks, and the entries it records.
    public static TheoryData<string, string, string?, string, int> ItemWrites => new()
    {
        // The move, at the statement that holds the project, at the one that holds the item, at the one that rewrites
        // its column and position, and at the save that renumbers its siblings.
        { "POST", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}/move", """{"status":"Done","order":0}""", WorkItemUpdateMySqlTests.HoldProject, 1 },
        { "POST", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}/move", """{"status":"Done","order":0}""", HoldOneItem, 1 },
        { "POST", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}/move", """{"status":"Done","order":0}""", AdvanceVersion, 1 },
        { "POST", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}/move", """{"status":"Done","order":0}""", "UPDATE `WorkItems` SET `Order`", 1 },
        // The update that names a status, which holds the project first as well.
        { "PATCH", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}", """{"status":"Done"}""", WorkItemUpdateMySqlTests.HoldProject, 1 },
        { "PATCH", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}", """{"status":"Done"}""", AdvanceVersion, 1 },
        // The save that moves the item to its new column, after the version was advanced.
        { "PATCH", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}", """{"status":"Done"}""", "UPDATE `WorkItems` SET `Status`", 1 },
        // The delete of an item, which holds the project first as well. One entry for the item that was blocked, one
        // for the item that was deleted.
        { "DELETE", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}", null, WorkItemUpdateMySqlTests.HoldProject, 2 },
        { "DELETE", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}", null, HoldOneItem, 2 },
        { "DELETE", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}", null, "DELETE FROM `WorkItems`", 2 }
    };

    [Theory]
    [MemberData(nameof(ItemWrites))]
    public async Task A_write_to_one_item_that_was_the_victim_of_one_deadlock_is_run_again_and_recorded_once(string method, string path, string? body, string statement, int entries)
    {
        var deadlocks = 1;
        var database = ItemDatabase(statement, () => deadlocks-- > 0);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, method, path, body);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();
        // The statement that deadlocked was sent twice, and so was the first statement of the transaction: the whole
        // write was run again, not the rest of it.
        Assert.Equal(2, texts.Count(text => text.Contains(statement, StringComparison.Ordinal)));
        var first = texts.First(text => text.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.Equal(2, texts.Count(text => text == first));
        // One entry per change, whatever the attempt before it did: the server rolled that one back.
        var insert = Assert.Single(texts, text => text.Contains("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal));
        Assert.Equal(entries, insert.Split("INSERT INTO `WorkItemChanges`").Length - 1);
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Theory]
    [MemberData(nameof(ItemWrites))]
    public async Task A_write_to_one_item_that_deadlocks_three_times_is_refused_busy_and_records_nothing(string method, string path, string? body, string statement, int entries)
    {
        var database = ItemDatabase(statement, () => true);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, method, path, body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
        Assert.Equal(KanbanErrorCodes.Busy, error!.Code);
        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        var texts = database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();
        // The first attempt and two more.
        Assert.Equal(3, texts.Count(text => text.Contains(statement, StringComparison.Ordinal)));
        Assert.True(entries > 0);
        Assert.DoesNotContain(texts, text => text.Contains("INSERT INTO", StringComparison.Ordinal));
        Assert.DoesNotContain($"GET {WorkItemChangeLog.LockName}", database.Locks);
    }

    private const string RenumberSibling = "UPDATE `WorkItems` SET `Order`";

    // The UPDATE that renumbers a sibling matches no row: another writer deleted the sibling after the move read it.
    // MySql.EntityFrameworkCore then throws DbUpdateConcurrencyException, which is not a deadlock.
    [Fact]
    public async Task A_move_whose_sibling_was_deleted_before_it_was_renumbered_is_run_again_and_recorded_once()
    {
        var vanished = 1;
        var database = ItemDatabase("no statement deadlocks", () => false, siblingIsGone: () => vanished-- > 0);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, "POST", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}/move", """{"status":"Done","order":0}""");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();
        // The whole move was run again, from the statement that holds the project.
        Assert.Equal(2, texts.Count(text => text.Contains(RenumberSibling, StringComparison.Ordinal)));
        Assert.Equal(2, texts.Count(text => text == WorkItemUpdateMySqlTests.HoldProject));
        Assert.Single(texts, text => text.Contains("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal));
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Fact]
    public async Task A_move_whose_sibling_is_gone_on_every_attempt_is_refused_busy_and_records_nothing()
    {
        var database = ItemDatabase("no statement deadlocks", () => false, siblingIsGone: () => true);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, "POST", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}/move", """{"status":"Done","order":0}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
        Assert.Equal(KanbanErrorCodes.Busy, error!.Code);
        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        var texts = database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();
        Assert.Equal(WorkItemChangeLog.DeadlockAttempts, texts.Count(text => text.Contains(RenumberSibling, StringComparison.Ordinal)));
        Assert.DoesNotContain(texts, text => text.Contains("INSERT INTO", StringComparison.Ordinal));
        Assert.DoesNotContain($"GET {WorkItemChangeLog.LockName}", database.Locks);
    }

    // The item has no dependencies to return with it once the write is done; the delete still finds the one it blocks.
    private static FakeDatabase ItemDatabase(string statement, Func<bool> deadlock, Func<bool>? siblingIsGone = null) =>
        new(lockResult: 1, command =>
            command.CommandText.Contains("`Title`", StringComparison.Ordinal) && command.CommandText.Contains("FROM `WorkItemDependencies`", StringComparison.Ordinal) ? null
            : command.CommandText.Contains(RenumberSibling, StringComparison.Ordinal) && siblingIsGone?.Invoke() == true ? Row("SELECT `ROW_COUNT()`", new() { ["ROW_COUNT()"] = 0 })
            : Answer(command))
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.ReplaceLineEndings("\n").Contains(statement, StringComparison.Ordinal) && deadlock() ? MySqlFailure(1213) : null
        };

    private static async Task<HttpResponseMessage> SendAsync(FakeBackedApplication api, string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        return await api.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_deadlock_is_not_run_again()
    {
        var database = new FakeDatabase(lockResult: 1, Answer)
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.Contains(HoldItems, StringComparison.Ordinal) ? MySqlFailure(1205) : null
        };
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, "DELETE", $"/api/projects/{ProjectId}", false);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(database.Commands, command => command.Text.Contains(HoldItems, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_a_project_holds_its_items_and_the_dependencies_they_block_before_it_reads_anything()
    {
        var database = Database(() => false);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, "DELETE", $"/api/projects/{ProjectId}", false);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var texts = database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();
        Assert.True(texts.Count == 10, string.Join("\n---\n", texts));
        // The project's row first, as in every write that changes which items a column holds: a move into one of
        // the project's columns has committed or has not started.
        Assert.Equal(WorkItemUpdateMySqlTests.HoldProject, texts[0]);
        Assert.Equal("UPDATE `WorkItems` AS `w`\nSET `Version` = `w`.`Version`\nWHERE `w`.`ProjectId` = @projectId", texts[1]);
        // Written by hand: starts from the project's items, which are held, and reaches the edges through their index.
        Assert.Equal(HoldBlockedByProject, texts[2]);
        // The first read comes after all three: what it sees cannot change before the delete commits.
        Assert.Equal(0, database.TransactionStarts[0]);
        Assert.All(texts.Skip(3).Take(3), text => Assert.StartsWith("SELECT ", text, StringComparison.Ordinal));
        Assert.Contains("FROM `Projects`", texts[3], StringComparison.Ordinal);
        Assert.Contains("FROM `WorkItemDependencies` AS `w`\nINNER JOIN `WorkItems` AS `w0` ON `w`.`BlockerItemId` = `w0`.`Id`", texts[5], StringComparison.Ordinal);
        Assert.StartsWith("DELETE FROM `Projects`", texts[6], StringComparison.Ordinal);
        Assert.Empty(WithoutChangeLogEntry(texts.Skip(7).ToList(), readsProject: false));
    }

    [Fact]
    public async Task Deleting_an_item_holds_its_project_then_it_and_the_dependencies_it_blocks_before_it_reads_anything_and_records_both()
    {
        var database = Database(() => false);
        using var api = new FakeBackedApplication(database);

        using var response = await SendAsync(api, "DELETE", $"/api/items/{WorkItemUpdateMySqlTests.ItemId}", false);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var texts = database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();
        Assert.True(texts.Count == 10, string.Join("\n---\n", texts));
        // The item's project is read before the transaction, and its row is the first thing the transaction holds,
        // as in a move: the delete takes the item out of a column that a move may be renumbering.
        Assert.Equal("SELECT `w`.`ProjectId`\nFROM `WorkItems` AS `w`\nWHERE `w`.`Id` = @itemId\nLIMIT 1", texts[0]);
        Assert.Equal(1, database.TransactionStarts[0]);
        Assert.Equal(WorkItemUpdateMySqlTests.HoldProject, texts[1]);
        Assert.Equal("UPDATE `WorkItems` AS `w`\nSET `Version` = `w`.`Version`\nWHERE `w`.`Id` = @itemId", texts[2]);
        // IX_WorkItemDependencies_BlockerItemId: only the edges in which the item blocks another one.
        Assert.Equal("UPDATE `WorkItemDependencies` AS `w`\nSET `BlockerItemId` = `w`.`BlockerItemId`\nWHERE `w`.`BlockerItemId` = @itemId", texts[3]);
        Assert.StartsWith("SELECT ", texts[4], StringComparison.Ordinal);
        Assert.StartsWith("SELECT `w`.`BlockedItemId`, `w`.`BlockerItemId`, `w0`.`ProjectId`\nFROM `WorkItemDependencies` AS `w`", texts[5], StringComparison.Ordinal);
        Assert.StartsWith("DELETE FROM `WorkItems`", texts[6], StringComparison.Ordinal);
        // One entry for the item that was blocked, one for the item that was deleted.
        Assert.Equal(2, texts[8].Split("INSERT INTO `WorkItemChanges`").Length - 1);
        Assert.Empty(WithoutChangeLogEntry(texts.Skip(7).ToList(), readsProject: false));
    }

    private static FakeDatabase Database(Func<bool> deadlock) =>
        new(lockResult: 1, Answer)
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.Contains(HoldItems, StringComparison.Ordinal) && deadlock() ? MySqlFailure(1213) : null
        };

    // MySqlException has no public constructor that takes the server's error number.
    internal static MySqlException MySqlFailure(int number) =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int)])!
            .Invoke(["Deadlock found when trying to get lock; try restarting transaction", number]);

    private static async Task<HttpResponseMessage> SendAsync(FakeBackedApplication api, string method, string path, bool hasBody)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (hasBody)
        {
            request.Content = JsonContent.Create(new UpdateEpicRequest("Epic", null, true));
        }

        return await api.CreateClient().SendAsync(request);
    }

    // One project with one epic and one item, which blocks an item of another project.
    private static DataTable? Answer(DbCommand command)
    {
        var text = command.CommandText.ReplaceLineEndings("\n").TrimEnd();
        if (text.StartsWith("SELECT EXISTS", StringComparison.Ordinal))
        {
            return Row("SELECT `e`", new() { ["e"] = true });
        }

        if (text.StartsWith("SELECT ", StringComparison.Ordinal) && text.Contains("FROM `WorkItemDependencies`", StringComparison.Ordinal))
        {
            return Row("SELECT `BlockedItemId`, `BlockerItemId`, `ProjectId`", new()
            {
                ["BlockedItemId"] = BlockedItemId.ToString(),
                ["BlockerItemId"] = WorkItemUpdateMySqlTests.ItemId.ToString(),
                ["ProjectId"] = OtherProjectId.ToString()
            });
        }

        if (text.StartsWith("SELECT ", StringComparison.Ordinal) && text.Contains("FROM `Projects`", StringComparison.Ordinal))
        {
            return Row(text, new() { ["Id"] = ProjectId.ToString(), ["CreatedAtUtc"] = Created, ["IsArchived"] = false, ["Key"] = "APP", ["Name"] = "App" });
        }

        if (text.StartsWith("SELECT ", StringComparison.Ordinal) && text.Contains("FROM `Epics`", StringComparison.Ordinal))
        {
            return Row(text, new()
            {
                ["Id"] = EpicId.ToString(), ["ProjectId"] = ProjectId.ToString(), ["Name"] = "Epic", ["IsArchived"] = false,
                ["CreatedAtUtc"] = Created, ["UpdatedAtUtc"] = Created
            });
        }

        return WorkItemUpdateMySqlTests.Answer(command);
    }

    // A row with the columns the SELECT lists on its first line, by name; a column without a value is NULL.
    private static DataTable Row(string select, Dictionary<string, object> values)
    {
        var firstLine = select.Split('\n')[0];
        var table = new DataTable();
        var columns = firstLine["SELECT ".Length..].Split(", ").Select(column => column[(column.IndexOf('.', StringComparison.Ordinal) + 1)..].Trim('`')).ToList();
        foreach (var column in columns)
        {
            table.Columns.Add(column, typeof(object));
        }

        table.Rows.Add(columns.Select(column => values.GetValueOrDefault(column, DBNull.Value)).ToArray());
        return table;
    }

    // Program.cs as it is, with every connection, command and transaction answered by FakeDatabase.
    internal sealed class FakeBackedApplication(FakeDatabase database) : WebApplicationFactory<KanbanDbContext>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Kanban", "Server=unused;Database=unused");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Auth:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                foreach (var registration in services
                    .Where(service => service.ServiceType == typeof(KanbanDbContext) ||
                        (service.ServiceType.IsGenericType && service.ServiceType.GenericTypeArguments.Contains(typeof(KanbanDbContext))))
                    .ToList())
                {
                    services.Remove(registration);
                }

                services.AddScoped(_ => CreatePipelineContext(new DatabaseOptions(), database));
            });
        }
    }
}
