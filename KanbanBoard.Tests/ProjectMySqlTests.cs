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
using MySql.Data.MySqlClient;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The SQL that MySql.EntityFrameworkCore writes for the project list and the project writes, which the SQLite tests
// never see. The provider runs for real; FakeDatabase records each command and answers it.
public sealed class ProjectMySqlTests
{
    private const string Summary =
        "SELECT `p`.`Id`, `p`.`Name`, `p`.`Key`, `p`.`Description`, `p`.`IsArchived`, `p`.`CreatedAtUtc`, (\n" +
        "    SELECT COUNT(*)\n    FROM `WorkItems` AS `w`\n    WHERE `p`.`Id` = `w`.`ProjectId`), (\n" +
        "    SELECT COUNT(*)\n    FROM `WorkItems` AS `w0`\n    WHERE (`p`.`Id` = `w0`.`ProjectId`) AND `w0`.`Status` NOT IN (4, 5)), `p`.`RepositoryUrl`, `p`.`DefaultBranch`\n" +
        "FROM `Projects` AS `p`\n";

    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string KeyHash = IdempotencyKeys.Hash(null, "plan-1");

    [Fact]
    public async Task The_list_reads_the_repository_url_and_default_branch_with_the_counts_in_one_statement()
    {
        var database = new FakeDatabase(lockResult: 1, Answer());
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().GetAsync("/api/projects");

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Contains("\"repository_url\":\"git@github.com:acme/app.git\",\"default_branch\":null", body, StringComparison.Ordinal);
        Assert.Equal(Summary + "WHERE NOT (`p`.`IsArchived`)\nORDER BY `p`.`Name`", Normalize(Assert.Single(database.Commands).Text));
    }

    [Fact]
    public async Task A_keyed_create_takes_the_named_lock_looks_for_the_key_inserts_and_releases_the_lock()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var project = new Project { Name = "App", Key = "APP", RepositoryUrl = "git@github.com:acme/app.git", DefaultBranch = "main" };

        var result = await Projects.CreateAsync(dbContext, project, KeyHash, CancellationToken.None);

        Assert.Equal(new ProjectWriteResult(ProjectWriteOutcome.Succeeded, project.Id), result);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 5, string.Join("\n---\n", texts));
        Assert.Equal("SELECT CAST(GET_LOCK(@p0, @p1) AS SIGNED) AS `Value`", texts[0]);
        Assert.Equal("SELECT `p`.`Id`\nFROM `Projects` AS `p`\nWHERE `p`.`IdempotencyKeyHash` = @idempotencyKeyHash\nLIMIT 1", texts[1]);
        Assert.Matches(@"^SELECT EXISTS \(\n    SELECT 1\n    FROM `Projects` AS `p`\n    WHERE `p`\.`Key` = @\w+\)$", texts[2]);
        Assert.StartsWith(
            "INSERT INTO `Projects` (`Id`, `CreatedAtUtc`, `DefaultBranch`, `Description`, `IdempotencyKeyHash`, `IsArchived`, `Key`, `Name`, `RepositoryUrl`)",
            texts[3], StringComparison.Ordinal);
        Assert.Equal("SELECT CAST(RELEASE_LOCK(@p0) AS SIGNED) AS `Value`", texts[4]);
        Assert.Equal(KeyHash, project.IdempotencyKeyHash);
    }

    [Fact]
    public async Task A_keyed_create_whose_key_has_a_project_reports_it_and_inserts_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: true)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await Projects.CreateAsync(dbContext, new Project { Name = "Other", Key = "OTHER" }, KeyHash, CancellationToken.None);

        Assert.Equal(new ProjectWriteResult(ProjectWriteOutcome.Unchanged, ProjectId), result);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.DoesNotContain(texts, text => text.Contains("INSERT", StringComparison.Ordinal));
        Assert.StartsWith("SELECT CAST(RELEASE_LOCK(", texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_keyed_create_that_does_not_get_the_lock_is_busy_reads_nothing_and_still_releases()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(lockResult: 0)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await Projects.CreateAsync(dbContext, new Project { Name = "App", Key = "APP" }, KeyHash, CancellationToken.None);

        Assert.Equal(ProjectWriteOutcome.Busy, result.Outcome);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 2, string.Join("\n---\n", texts));
        Assert.StartsWith("SELECT CAST(GET_LOCK(", texts[0], StringComparison.Ordinal);
        Assert.StartsWith("SELECT CAST(RELEASE_LOCK(", texts[1], StringComparison.Ordinal);
    }

    // On MariaDB the keyed create's transaction is REPEATABLE READ: once it has read, it does not see a project
    // that another writer (a create without a key, or a rename) commits afterwards, but its INSERT still fails on
    // IX_Projects_Key. The fake answers as that snapshot would: "is the key taken?" stays no, and the INSERT fails.
    [Fact]
    public async Task A_keyed_create_that_loses_the_project_key_to_a_concurrent_writer_is_key_taken_and_releases_the_lock()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyTakenByConcurrentWriter: true)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await Projects.CreateAsync(dbContext, new Project { Name = "App", Key = "APP" }, KeyHash, CancellationToken.None);

        Assert.Equal(new ProjectWriteResult(ProjectWriteOutcome.KeyTaken), result);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 5, string.Join("\n---\n", texts));
        Assert.StartsWith("INSERT INTO `Projects`", texts[3], StringComparison.Ordinal);
        Assert.StartsWith("SELECT CAST(RELEASE_LOCK(", texts[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_keyed_create_that_loses_the_project_key_to_a_concurrent_writer_answers_409_conflict()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyTakenByConcurrentWriter: true)) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PostAsJsonAsync("/api/projects", new { name = "App", key = "APP", idempotencyKey = "plan-1" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, body);
        Assert.Contains("\"code\":\"conflict\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rename_that_loses_the_project_key_to_a_concurrent_writer_answers_409_conflict()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyTakenByConcurrentWriter: true)) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PatchAsJsonAsync($"/api/projects/{ProjectId}", new { key = "TAKEN" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, body);
        Assert.Contains("\"code\":\"conflict\"", body, StringComparison.Ordinal);
        Assert.Contains(database.Commands, command => command.Text.StartsWith("UPDATE `Projects` SET `Key` = ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_create_without_a_key_takes_no_lock()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await Projects.CreateAsync(dbContext, new Project { Name = "App", Key = "APP" }, null, CancellationToken.None);

        Assert.Equal(ProjectWriteOutcome.Succeeded, result.Outcome);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 2, string.Join("\n---\n", texts));
        Assert.DoesNotContain(texts, text => text.Contains("_LOCK(", StringComparison.Ordinal) || text.Contains("IdempotencyKeyHash` =", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_partial_update_writes_only_the_columns_it_changes_and_reads_the_summary_back()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PatchAsJsonAsync($"/api/projects/{ProjectId}", new { defaultBranch = "main", repositoryUrl = (string?)null });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.EndsWith("FROM `Projects` AS `p`\nWHERE `p`.`Id` = @projectId\nLIMIT 1", texts[0], StringComparison.Ordinal);
        Assert.Matches(@"^UPDATE `Projects` SET `DefaultBranch` = @\w+, `RepositoryUrl` = @\w+\nWHERE `Id` = @\w+;", texts[1]);
        Assert.Equal(Summary + "WHERE `p`.`Id` = @projectId\nORDER BY `p`.`Name`\nLIMIT 1", texts[2]);
    }

    [Fact]
    public void Migration_and_deploy_script_add_the_same_columns_and_unique_index()
    {
        var migration = new AddProjectRepositoryAndIdempotencyKey();
        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToList();
        var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        var script = File.ReadAllText(FindRepoFile("deploy/sql/007_project_repository_and_idempotency_key.sql")).ReplaceLineEndings("\n");

        // Existing projects keep their rows: every new column is nullable and nothing is altered or dropped.
        Assert.Equal(4, migration.UpOperations.Count);
        Assert.Equal(
            [("DefaultBranch", "varchar(255)"), ("IdempotencyKeyHash", "varchar(64)"), ("RepositoryUrl", "varchar(2048)")],
            columns.Select(column => (column.Name, column.ColumnType!)).ToArray());
        Assert.All(columns, column =>
        {
            Assert.Equal(("Projects", true), (column.Table, column.IsNullable));
            Assert.Contains($"ALTER TABLE `Projects` ADD COLUMN IF NOT EXISTS `{column.Name}` {column.ColumnType} NULL;", script, StringComparison.Ordinal);
        });
        // The same order as the migration, so both give the same table.
        Assert.True(
            script.IndexOf("`DefaultBranch`", StringComparison.Ordinal) < script.IndexOf("ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash`", StringComparison.Ordinal) &&
            script.IndexOf("ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash`", StringComparison.Ordinal) < script.IndexOf("`RepositoryUrl`", StringComparison.Ordinal));
        Assert.True(index.IsUnique);
        Assert.Equal(["IdempotencyKeyHash"], index.Columns);
        Assert.Contains($"CREATE UNIQUE INDEX IF NOT EXISTS `{index.Name}`\n    ON `Projects` (`IdempotencyKeyHash`);", script, StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n").TrimEnd();

    // MySqlException carries the server's error number only through an internal constructor.
    private static Exception ServerError(string message, int number) =>
        (Exception)Activator.CreateInstance(
            typeof(MySqlException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [message, number],
            culture: null)!;

    private static Func<DbCommand, DataTable?> Answer(bool keyUsed = false, int lockResult = 1, bool keyTakenByConcurrentWriter = false) => command =>
    {
        var text = Normalize(command.CommandText);
        if (keyTakenByConcurrentWriter &&
            (text.StartsWith("INSERT INTO `Projects`", StringComparison.Ordinal) || text.StartsWith("UPDATE `Projects`", StringComparison.Ordinal)))
        {
            throw ServerError("Duplicate entry 'TAKEN' for key 'IX_Projects_Key'", 1062);
        }

        if (text.Contains("_LOCK(", StringComparison.Ordinal))
        {
            return Table(("Value", (long)lockResult));
        }

        if (text.Contains("ROW_COUNT()", StringComparison.Ordinal))
        {
            return Table(("ROW_COUNT()", 1));
        }

        // "Does another project have the key?"
        if (text.StartsWith("SELECT EXISTS", StringComparison.Ordinal))
        {
            return Table(("e", false));
        }

        // The project the idempotency key already belongs to.
        if (text.Contains("`IdempotencyKeyHash` = ", StringComparison.Ordinal))
        {
            return keyUsed ? Table(("Id", ProjectId.ToString())) : null;
        }

        if (text.Contains("SELECT COUNT(*)", StringComparison.Ordinal))
        {
            return Table(
                ("Id", ProjectId.ToString()), ("Name", "App"), ("Key", "APP"), ("Description", DBNull.Value), ("IsArchived", false),
                ("CreatedAtUtc", new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc)), ("TotalItems", 3), ("OpenItems", 2),
                ("RepositoryUrl", "git@github.com:acme/app.git"), ("DefaultBranch", DBNull.Value));
        }

        // The tracked project a partial update loads.
        return text.Contains("FROM `Projects`", StringComparison.Ordinal)
            ? Table(
                ("Id", ProjectId.ToString()), ("CreatedAtUtc", new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc)), ("DefaultBranch", DBNull.Value),
                ("Description", DBNull.Value), ("IdempotencyKeyHash", DBNull.Value), ("IsArchived", false), ("Key", "APP"), ("Name", "App"),
                ("RepositoryUrl", "git@github.com:acme/app.git"))
            : null;
    };

    private static DataTable Table(params (string Name, object Value)[] row)
    {
        var table = new DataTable();
        foreach (var (name, value) in row)
        {
            table.Columns.Add(name, value is DBNull ? typeof(string) : value.GetType());
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
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapKanbanProjectEndpoints());
                });
            })
            .StartAsync();
}
