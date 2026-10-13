using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The SQL that MySql.EntityFrameworkCore writes for the dependency writes, which the SQLite tests never see.
// The provider runs for real; FakeDatabase records each command and answers it.
public sealed class WorkItemDependencyMySqlTests
{
    private static readonly Guid Blocked = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Blocker = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Remove_deletes_the_edge_with_a_statement_mariadb_accepts()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(lockResult: 1));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = await WorkItemDependencies.RemoveAsync(dbContext, Blocked, Blocker, null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(WorkItemDependencyOutcome.Succeeded, outcome);
        var delete = Assert.Single(database.Commands, command => command.Text.Contains("DELETE", StringComparison.Ordinal)).Text;
        // MariaDB has no DELETE FROM `t` AS `alias`, which is what ExecuteDelete produces.
        Assert.DoesNotContain("DELETE FROM `WorkItemDependencies` AS", delete, StringComparison.Ordinal);
        Assert.StartsWith("DELETE FROM `WorkItemDependencies`\nWHERE `BlockedItemId` = @", delete.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        // The blocked item's row is held before anything is read, so two removes of one edge run one after the other.
        Assert.Equal(
            "UPDATE `WorkItems` AS `w`\nSET `Version` = `w`.`Version`\nWHERE `w`.`Id` = @itemId",
            database.Commands[0].Text.ReplaceLineEndings("\n").TrimEnd());
        WithoutChangeLogEntry(database.Commands.Select(command => command.Text).ToList());
    }

    [Fact]
    public async Task Remove_of_an_edge_that_another_caller_deletes_first_still_succeeds()
    {
        // The edge is read, then the DELETE finds no row.
        var database = new FakeDatabase(lockResult: 1, Answer(lockResult: 1, rowsAffected: 0));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = await WorkItemDependencies.RemoveAsync(dbContext, Blocked, Blocker, null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(WorkItemDependencyOutcome.Succeeded, outcome);
        Assert.Contains(database.Commands, command => command.Text.Contains("DELETE FROM `WorkItemDependencies`", StringComparison.Ordinal));
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Add_takes_the_named_lock_before_the_insert_and_releases_it_after()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(lockResult: 1));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = await WorkItemDependencies.AddAsync(dbContext, Blocked, Blocker, null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(WorkItemDependencyOutcome.Succeeded, outcome);
        var all = database.Commands.Select(command => command.Text).ToList();
        // The entry of the change feed is recorded inside the dependency lock, under the lock of its own.
        var texts = WithoutChangeLogEntry(all.SkipLast(1).ToList());
        var getLock = Assert.Single(Indexes(texts, "GET_LOCK("));
        var insert = Assert.Single(Indexes(texts, "INSERT INTO `WorkItemDependencies`"));
        Assert.Equal(0, getLock);
        Assert.True(getLock < insert, string.Join("\n---\n", all));
        // Both items are held, in one statement, before anything is read and before the INSERT locks them for its
        // foreign keys: a move renumbering the two items at the same time then waits instead of deadlocking.
        Assert.Equal(
            "UPDATE `WorkItems` AS `w`\nSET `Version` = `w`.`Version`\nWHERE (`w`.`Id` = @blockedItemId) OR (`w`.`Id` = @blockerItemId)",
            texts[1].ReplaceLineEndings("\n").TrimEnd());
        Assert.Equal(
            [
                $"GET {WorkItemDependencies.LockName}", $"GET {WorkItemChangeLog.LockName}",
                $"RELEASE {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemDependencies.LockName}"
            ],
            LocksAroundTheCommit(database));
        Assert.Contains("RELEASE_LOCK(", all[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_that_does_not_get_the_named_lock_is_refused_busy_and_writes_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(lockResult: 0));
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PostAsJsonAsync(
            $"/api/items/{Blocked}/dependencies", new AddWorkItemDependencyRequest(Blocker));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
        Assert.Equal(KanbanErrorCodes.Busy, error!.Code);
        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        // Nothing but the lock attempt and its release reached the database.
        Assert.Equal(2, database.Commands.Count);
        Assert.Contains("GET_LOCK(", database.Commands[0].Text, StringComparison.Ordinal);
        Assert.Contains("RELEASE_LOCK(", database.Commands[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_releases_the_named_lock_when_taking_it_is_interrupted()
    {
        // The server may have granted the lock even though the client never read the answer.
        var database = new FakeDatabase(lockResult: 1, command =>
            command.CommandText.Contains("GET_LOCK(", StringComparison.Ordinal)
                ? throw new OperationCanceledException("aborted while waiting for GET_LOCK")
                : Answer(lockResult: 1)(command));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkItemDependencies.AddAsync(dbContext, Blocked, Blocker, null, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains("RELEASE_LOCK(", database.Commands[^1].Text, StringComparison.Ordinal);
    }

    // The statement that holds the items, and the one that writes the edge.
    public static TheoryData<bool, string> DeadlockingStatements => new()
    {
        { true, "UPDATE `WorkItems` AS `w`" },
        { true, "INSERT INTO `WorkItemDependencies`" },
        { false, "UPDATE `WorkItems` AS `w`" },
        { false, "DELETE FROM `WorkItemDependencies`" }
    };

    [Theory]
    [MemberData(nameof(DeadlockingStatements))]
    public async Task A_dependency_write_that_was_the_victim_of_one_deadlock_is_run_again_and_recorded_once(bool add, string statement)
    {
        var deadlocks = 1;
        var database = Deadlocking(statement, () => deadlocks-- > 0);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = add
            ? await WorkItemDependencies.AddAsync(dbContext, Blocked, Blocker, null, DateTimeOffset.UtcNow, CancellationToken.None)
            : await WorkItemDependencies.RemoveAsync(dbContext, Blocked, Blocker, null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(WorkItemDependencyOutcome.Succeeded, outcome);
        var texts = database.Commands.Select(command => command.Text).ToList();
        Assert.Equal(2, Indexes(texts, statement).Count);
        // The whole write was run again, from the statement that holds the items.
        Assert.Equal(2, Indexes(texts, "UPDATE `WorkItems` AS `w`").Count);
        Assert.Single(Indexes(texts, "INSERT INTO `WorkItemChanges`"));
        // An add keeps the dependency lock over both attempts.
        Assert.Equal(add ? 1 : 0, database.Locks.Count(entry => entry == $"GET {WorkItemDependencies.LockName}"));
        Assert.Empty(dbContext.ChangeTracker.Entries().Where(entry => entry.State != Microsoft.EntityFrameworkCore.EntityState.Unchanged));
    }

    [Theory]
    [MemberData(nameof(DeadlockingStatements))]
    public async Task A_dependency_write_that_deadlocks_three_times_is_refused_busy_and_records_nothing(bool add, string statement)
    {
        var database = Deadlocking(statement, () => true);
        using var host = await StartApiAsync(database);

        using var response = add
            ? await host.GetTestClient().PostAsJsonAsync($"/api/items/{Blocked}/dependencies", new AddWorkItemDependencyRequest(Blocker))
            : await host.GetTestClient().DeleteAsync($"/api/items/{Blocked}/dependencies/{Blocker}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<KanbanErrorDto>();
        Assert.Equal(KanbanErrorCodes.Busy, error!.Code);
        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        var texts = database.Commands.Select(command => command.Text).ToList();
        Assert.Equal(3, Indexes(texts, statement).Count);
        Assert.Empty(Indexes(texts, "INSERT INTO `WorkItemChanges`"));
        // The dependency lock is given back.
        Assert.Equal(add, texts[^1].Contains("RELEASE_LOCK(", StringComparison.Ordinal));
    }

    private static FakeDatabase Deadlocking(string statement, Func<bool> deadlock) =>
        new(lockResult: 1, Answer(lockResult: 1))
        {
            Fail = command => command.CommandText.StartsWith(statement, StringComparison.Ordinal) && deadlock()
                ? WorkItemDeadlockRetryMySqlTests.MySqlFailure(1213)
                : null
        };

    private static List<int> Indexes(List<string> texts, string fragment) =>
        Enumerable.Range(0, texts.Count).Where(index => texts[index].Contains(fragment, StringComparison.Ordinal)).ToList();

    // A database in which both items exist and no edge closes a cycle.
    private static Func<DbCommand, DataTable?> Answer(int lockResult, int rowsAffected = 1) => command =>
    {
        var text = command.CommandText;
        if (text.Contains("GET_LOCK(", StringComparison.Ordinal))
        {
            return Table(("Value", (long)lockResult));
        }

        if (text.Contains("RELEASE_LOCK(", StringComparison.Ordinal))
        {
            return Table(("Value", 1L));
        }

        if (text.Contains("COUNT(*)", StringComparison.Ordinal))
        {
            return Table(("c", 2));
        }

        if (text.Contains("ROW_COUNT()", StringComparison.Ordinal) && !text.Contains("`WorkItemChanges`", StringComparison.Ordinal))
        {
            return Table(("ROW_COUNT()", rowsAffected));
        }

        // "Is the edge already stored?" in AddAsync: not yet.
        if (text.Contains("EXISTS", StringComparison.Ordinal))
        {
            return Table(("e", false));
        }

        // FindAsync of the edge in RemoveAsync.
        if (text.Contains("`BlockedItemId` = @", StringComparison.Ordinal) && text.Contains("`BlockerItemId` = @", StringComparison.Ordinal) &&
            text.StartsWith("SELECT", StringComparison.Ordinal))
        {
            return Table(("BlockedItemId", Blocked.ToString()), ("BlockerItemId", Blocker.ToString()));
        }

        return null;
    };

    private static DataTable Table(params (string Name, object Value)[] row)
    {
        var table = new DataTable();
        foreach (var (name, value) in row)
        {
            table.Columns.Add(name, value.GetType());
        }

        table.Rows.Add(row.Select(cell => cell.Value).ToArray());
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
                    // As in Program.cs: a write that gave up after deadlocks is answered 503 busy.
                    app.UseKanbanBusyRefusal();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapKanbanDependencyEndpoints());
                });
            })
            .StartAsync();
}
