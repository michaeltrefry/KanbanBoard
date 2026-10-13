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

// The SQL that MySql.EntityFrameworkCore writes for the comment writes and the comment pages, which the SQLite
// tests never see. The provider runs for real; FakeDatabase records each command and answers it.
public sealed class WorkItemCommentMySqlTests
{
    private const string StampItem = "UPDATE `WorkItems` AS `w`\nSET `UpdatedAtUtc` = @p\nWHERE `w`.`Id` = @itemId";
    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CommentId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Add_locks_the_item_row_before_it_looks_for_the_key_reads_the_latest_comment_and_inserts()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: false, latest: null)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await WorkItemComments.AddAsync(dbContext, ItemId, UserId, "Factory", "note", "run-1", null, Now, CancellationToken.None);

        Assert.Equal(WorkItemCommentOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.CommentId);
        var texts = WithoutChangeLogEntry(database.Commands.Select(command => Normalize(command.Text)).ToList());
        Assert.True(texts.Count == 4, string.Join("\n---\n", texts));
        // The UPDATE comes first: it is what makes two adds with one key run one after the other, and what keeps
        // two writers of one item from deadlocking on the shared lock the INSERT takes for its foreign key.
        Assert.StartsWith(StampItem, texts[0], StringComparison.Ordinal);
        Assert.Matches(@"FROM `WorkItemComments` AS `w`\nWHERE \(`w`\.`WorkItemId` = @\w+\) AND \(`w`\.`IdempotencyKeyHash` = @keyHash\)\nLIMIT 1$", texts[1]);
        Assert.Matches(
            @"^SELECT `w`\.`CreatedAtUtc`\nFROM `WorkItemComments` AS `w`\nWHERE `w`\.`WorkItemId` = @\w+\nORDER BY `w`\.`CreatedAtUtc` DESC\nLIMIT 1$", texts[2]);
        Assert.StartsWith(
            "INSERT INTO `WorkItemComments` (`Id`, `Author`, `AuthorUserId`, `Body`, `CreatedAtUtc`, `IdempotencyKeyHash`, `WorkItemId`)", texts[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_without_a_key_does_not_look_for_one()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: true, latest: null)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await WorkItemComments.AddAsync(dbContext, ItemId, null, "tester", "note", null, null, Now, CancellationToken.None);

        Assert.Equal(WorkItemCommentOutcome.Succeeded, result.Outcome);
        var texts = WithoutChangeLogEntry(database.Commands.Select(command => Normalize(command.Text)).ToList());
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.All(texts.Take(2), text => Assert.DoesNotContain("IdempotencyKeyHash", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_with_a_key_the_item_already_carries_reports_that_comment_and_inserts_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: true, latest: null)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await WorkItemComments.AddAsync(dbContext, ItemId, UserId, "Factory", "another body", "run-1", null, Now, CancellationToken.None);

        Assert.Equal(new WorkItemCommentResult(WorkItemCommentOutcome.Unchanged, CommentId), result);
        Assert.Equal(2, database.Commands.Count);
        Assert.DoesNotContain(database.Commands, command => command.Text.Contains("INSERT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_when_the_latest_comment_is_not_older_stamps_the_item_again_with_the_later_time()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: false, latest: Now.UtcDateTime.AddSeconds(5))) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await WorkItemComments.AddAsync(dbContext, ItemId, UserId, "Factory", "note", null, null, Now, CancellationToken.None);

        Assert.Equal(WorkItemCommentOutcome.Succeeded, result.Outcome);
        var texts = WithoutChangeLogEntry(database.Commands.Select(command => Normalize(command.Text)).ToList());
        Assert.True(texts.Count == 4, string.Join("\n---\n", texts));
        Assert.StartsWith(StampItem, texts[0], StringComparison.Ordinal);
        Assert.StartsWith(StampItem, texts[2], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO `WorkItemComments`", texts[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_to_an_item_the_update_does_not_match_is_not_found_and_reads_nothing_more()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: false, latest: null));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await WorkItemComments.AddAsync(dbContext, ItemId, UserId, "Factory", "note", "run-1", null, Now, CancellationToken.None);

        Assert.Equal(WorkItemCommentOutcome.NotFound, result.Outcome);
        Assert.StartsWith("UPDATE `WorkItems`", Assert.Single(database.Commands).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_of_comments_is_a_keyset_query_on_the_item()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: false, latest: null, itemExists: true));
        using var host = await StartApiAsync(database);
        var cursor = new WorkItemCursor(Now, CommentId).Encode(WorkItemCursor.Comments);

        var response = await host.GetTestClient().GetAsync($"/api/items/{ItemId}/comments?limit=2&cursor={cursor}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("""{"items":[],"next_cursor":null}""", body);
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 2, string.Join("\n---\n", texts));
        Assert.Contains("FROM `WorkItems` AS `w`\n    WHERE `w`.`Id` = @itemId", texts[0], StringComparison.Ordinal);
        Assert.EndsWith(
            "FROM `WorkItemComments` AS `w`\n" +
            "WHERE (`w`.`WorkItemId` = @itemId) AND ((`w`.`CreatedAtUtc` > @createdAtUtc) OR ((`w`.`CreatedAtUtc` = @createdAtUtc) AND (`w`.`Id` > @id)))\n" +
            "ORDER BY `w`.`CreatedAtUtc`, `w`.`Id`\nLIMIT @p",
            texts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_endpoint_writes_the_comment_and_reads_the_item_back()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: false, latest: null)) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PostAsJsonAsync($"/api/items/{ItemId}/comments", new CreateWorkItemCommentRequest("tester", "note", "run-1"));

        // The fake database has no item to read back after the write.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(database.Commands, command => command.Text.StartsWith("INSERT INTO `WorkItemComments`", StringComparison.Ordinal));
    }

    [Fact]
    public void Migration_and_deploy_script_add_the_same_columns_and_unique_index()
    {
        var migration = new AddWorkItemCommentAuthorAndIdempotencyKey();
        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToList();
        var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        var author = Assert.Single(migration.UpOperations.OfType<AlterColumnOperation>());
        var script = File.ReadAllText(FindRepoFile("deploy/sql/006_work_item_comment_author_and_idempotency_key.sql"));

        // Existing comments keep their rows: both new columns are nullable and nothing is dropped.
        Assert.Equal(4, migration.UpOperations.Count);
        Assert.Equal(["AuthorUserId", "IdempotencyKeyHash"], columns.Select(column => column.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(columns, column =>
        {
            Assert.True(column.IsNullable);
            Assert.Contains($"ADD COLUMN IF NOT EXISTS `{column.Name}` {column.ColumnType} NULL;", script, StringComparison.Ordinal);
        });
        Assert.Equal(("Author", "varchar(320)", false), (author.Name, author.ColumnType, author.IsNullable));
        Assert.Contains("MODIFY COLUMN `Author` varchar(320) NOT NULL;", script, StringComparison.Ordinal);
        Assert.True(index.IsUnique);
        Assert.Equal(["WorkItemId", "IdempotencyKeyHash"], index.Columns);
        Assert.Contains($"CREATE UNIQUE INDEX IF NOT EXISTS `{index.Name}`\n    ON `WorkItemComments` (`WorkItemId`, `IdempotencyKeyHash`);", script.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n");

    private static Func<DbCommand, DataTable?> Answer(bool keyUsed, DateTime? latest, bool itemExists = false) => command =>
    {
        var text = Normalize(command.CommandText);
        if (text.Contains("ROW_COUNT()", StringComparison.Ordinal))
        {
            return Table(("ROW_COUNT()", 1));
        }

        // "Does the item exist?" in the list endpoint.
        if (text.Contains("EXISTS", StringComparison.Ordinal) && text.Contains("FROM `WorkItems`", StringComparison.Ordinal))
        {
            return Table(("e", itemExists));
        }

        if (!text.Contains("FROM `WorkItemComments`", StringComparison.Ordinal))
        {
            return null;
        }

        // The comment the key already belongs to.
        if (text.Contains("`IdempotencyKeyHash` = ", StringComparison.Ordinal))
        {
            return keyUsed
                ? Table(
                    ("Id", CommentId.ToString()), ("Author", "Factory"), ("AuthorUserId", UserId.ToString()), ("Body", "note"),
                    ("CreatedAtUtc", Now.UtcDateTime), ("IdempotencyKeyHash", IdempotencyKeys.Hash(UserId, "run-1")),
                    ("WorkItemId", ItemId.ToString()))
                : null;
        }

        // The time of the item's latest comment.
        return text.StartsWith("SELECT `w`.`CreatedAtUtc`\n", StringComparison.Ordinal) && latest is not null
            ? Table(("CreatedAtUtc", latest.Value))
            : null;
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
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapKanbanCommentEndpoints());
                });
            })
            .StartAsync();
}
