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

// The SQL that MySql.EntityFrameworkCore writes for the link writes and reads, which the SQLite tests never see.
// The provider runs for real; FakeDatabase records each command and answers it.
public sealed class WorkItemLinkMySqlTests
{
    private const string Url = "https://github.com/acme/app/pull/42";
    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LinkId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Add_locks_the_item_row_before_it_looks_for_the_link_and_inserts_it()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(linkExists: false)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = await WorkItemLinks.AddAsync(dbContext, ItemId, "pr", Url, null, Now, CancellationToken.None);

        Assert.Equal(WorkItemLinkOutcome.Succeeded, outcome);
        var texts = WithoutChangeLogEntry(database.Commands.Select(command => Normalize(command.Text)).ToList());
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        // The UPDATE comes first: it is what makes two identical adds run one after the other.
        Assert.StartsWith("UPDATE `WorkItems` AS `w`\nSET `UpdatedAtUtc` = @p\nWHERE `w`.`Id` = @itemId", texts[0], StringComparison.Ordinal);
        Assert.Contains("FROM `WorkItemLinks` AS `w`\n    WHERE ((`w`.`WorkItemId` = @itemId) AND (`w`.`Kind` = @kind)) AND (`w`.`UrlHash` = @urlHash)", texts[1], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO `WorkItemLinks` (`Id`, `CreatedAtUtc`, `Kind`, `Url`, `UrlHash`, `WorkItemId`)", texts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_of_a_link_the_item_already_carries_inserts_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(linkExists: true)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = await WorkItemLinks.AddAsync(dbContext, ItemId, "pr", Url, null, Now, CancellationToken.None);

        Assert.Equal(WorkItemLinkOutcome.Unchanged, outcome);
        Assert.DoesNotContain(database.Commands, command => command.Text.Contains("INSERT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_to_an_item_the_update_does_not_match_is_not_found_and_reads_nothing_more()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(linkExists: false));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = await WorkItemLinks.AddAsync(dbContext, ItemId, "pr", Url, null, Now, CancellationToken.None);

        Assert.Equal(WorkItemLinkOutcome.NotFound, outcome);
        Assert.StartsWith("UPDATE `WorkItems`", Assert.Single(database.Commands).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remove_deletes_the_link_with_a_statement_mariadb_accepts(bool byLinkId)
    {
        var database = new FakeDatabase(lockResult: 1, Answer(linkExists: true)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var outcome = byLinkId
            ? await WorkItemLinks.RemoveAsync(dbContext, ItemId, LinkId, null, null, null, Now, CancellationToken.None)
            : await WorkItemLinks.RemoveAsync(dbContext, ItemId, null, "pr", Url, null, Now, CancellationToken.None);

        Assert.Equal(WorkItemLinkOutcome.Succeeded, outcome);
        var texts = WithoutChangeLogEntry(database.Commands.Select(command => Normalize(command.Text)).ToList());
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.StartsWith("UPDATE `WorkItems` AS `w`\nSET `UpdatedAtUtc` = @p\nWHERE `w`.`Id` = @itemId", texts[0], StringComparison.Ordinal);
        Assert.Matches(
            byLinkId
                ? @"FROM `WorkItemLinks` AS `w`\nWHERE \(`w`\.`Id` = @\w+\) AND \(`w`\.`WorkItemId` = @\w+\)\nLIMIT 1$"
                : @"FROM `WorkItemLinks` AS `w`\nWHERE \(\(`w`\.`WorkItemId` = @\w+\) AND \(`w`\.`Kind` = @\w+\)\) AND \(`w`\.`UrlHash` = @\w+\)\nLIMIT 1$",
            texts[1]);
        // MariaDB has no DELETE FROM `t` AS `alias`, which is what ExecuteDelete produces.
        Assert.DoesNotContain("DELETE FROM `WorkItemLinks` AS", texts[2], StringComparison.Ordinal);
        Assert.StartsWith("DELETE FROM `WorkItemLinks`\nWHERE `Id` = @", texts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_by_url_matches_the_hash_in_an_exists_and_loads_dependencies_and_links_in_one_statement_each()
    {
        var database = new FakeDatabase(lockResult: 1);
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().GetAsync($"/api/items/by-link?url={Uri.EscapeDataString(Url)}");

        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        const string carriesUrl = "WHERE (`w0`.`WorkItemId` = `w`.`Id`) AND (`w0`.`UrlHash` = @urlHash))";
        Assert.Contains("FROM `WorkItems` AS `w`", texts[0], StringComparison.Ordinal);
        Assert.Contains("FROM `WorkItemLinks` AS `w0`\n    " + carriesUrl, texts[0], StringComparison.Ordinal);
        Assert.Contains("FROM `WorkItemDependencies`", texts[1], StringComparison.Ordinal);
        Assert.StartsWith("SELECT `w`.`Id`, `w`.`CreatedAtUtc`, `w`.`Kind`, `w`.`Url`, `w`.`UrlHash`, `w`.`WorkItemId`\nFROM `WorkItemLinks` AS `w`\nWHERE EXISTS (", texts[2], StringComparison.Ordinal);
        // The URL itself is never compared: its column has the database's case-insensitive collation and no index.
        Assert.All(texts, text => Assert.DoesNotContain("`Url` =", text, StringComparison.Ordinal));
        // MySql.EntityFrameworkCore cannot translate Contains over a list of ids, and MariaDB has no LIMIT inside EXISTS/IN.
        Assert.All(texts, text => Assert.DoesNotContain(" IN (", text, StringComparison.Ordinal));
        Assert.All(texts, text => Assert.DoesNotContain("LIMIT", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_endpoint_answers_with_the_item_its_dependencies_and_its_links()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(linkExists: false)) { NonQueryResult = 1 };
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().PostAsJsonAsync($"/api/items/{ItemId}/links", new AddWorkItemLinkRequest("pr", Url));

        // The fake database has no item to read back after the write.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(database.Commands, command => command.Text.StartsWith("INSERT INTO `WorkItemLinks`", StringComparison.Ordinal));
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n");

    private static Func<DbCommand, DataTable?> Answer(bool linkExists) => command =>
    {
        var text = command.CommandText;
        if (text.Contains("ROW_COUNT()", StringComparison.Ordinal))
        {
            return Table(("ROW_COUNT()", 1));
        }

        if (!text.Contains("FROM `WorkItemLinks`", StringComparison.Ordinal))
        {
            return null;
        }

        // "Is the link already on the item?" in AddAsync.
        if (text.Contains("EXISTS", StringComparison.Ordinal))
        {
            return Table(("e", linkExists));
        }

        // The link that RemoveAsync reads before it deletes it.
        return linkExists
            ? Table(
                ("Id", LinkId.ToString()), ("CreatedAtUtc", Now.UtcDateTime), ("Kind", "pr"), ("Url", Url),
                ("UrlHash", WorkItemLinks.HashUrl(Url)), ("WorkItemId", ItemId.ToString()))
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
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapKanbanLinkEndpoints();
                        endpoints.MapKanbanWorkItemReadEndpoints();
                    });
                });
            })
            .StartAsync();
}
