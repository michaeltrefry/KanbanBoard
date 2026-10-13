using System.Data;
using System.Data.Common;
using System.Net;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The SQL that MySql.EntityFrameworkCore writes for the filters and the cursor of GET /api/items, which the SQLite
// tests never see. The provider runs for real; FakeDatabase records each command and answers it.
public sealed class WorkItemListMySqlTests
{
    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Created = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Filters_are_written_as_sql_that_does_not_treat_a_label_as_a_pattern()
    {
        var database = new FakeDatabase(lockResult: 1);
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().GetAsync(
            "/api/items?label=a_i&updatedSince=2026-10-01T08:00:00Z&claimState=unclaimed&unblockedOnly=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var select = Normalize(database.Commands[0].Text);
        // LOCATE compares characters; LIKE would read the '_' and '%' of a label as wildcards.
        Assert.Contains("LOCATE(@delimited, CONCAT(CONCAT(',', LOWER(`w`.`Labels`)), ',')) > 0", select, StringComparison.Ordinal);
        Assert.DoesNotContain("`Labels` LIKE", select, StringComparison.Ordinal);
        Assert.Contains("(`w`.`UpdatedAtUtc` >= @since)", select, StringComparison.Ordinal);
        Assert.Contains("(`w`.`ClaimExpiresAtUtc` IS NULL OR (`w`.`ClaimExpiresAtUtc` <= @now))", select, StringComparison.Ordinal);
        // The unpaged list keeps its board order and has no LIMIT.
        Assert.Contains("ORDER BY `w`.`Status`, `w`.`Order`", select, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", select, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claimed", "(`w`.`ClaimExpiresAtUtc` > @now)")]
    [InlineData("expired", "(`w`.`ClaimExpiresAtUtc` <= @now)")]
    public async Task Claim_state_is_compared_with_the_clock_in_sql(string state, string predicate)
    {
        var database = new FakeDatabase(lockResult: 1);
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().GetAsync($"/api/items?claimState={state}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(predicate, Normalize(database.Commands[0].Text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_is_a_keyset_query_and_its_dependencies_and_links_are_bounded_by_a_range_not_a_list_of_ids()
    {
        var database = new FakeDatabase(lockResult: 1, OneItem);
        using var host = await StartApiAsync(database);
        var cursor = new WorkItemCursor(new DateTimeOffset(Created).AddMinutes(-1), Guid.Empty).Encode(WorkItemCursor.WorkItems);

        var response = await host.GetTestClient().GetAsync($"/api/items?limit=1&includeComments=false&cursor={cursor}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Contains(ItemId.ToString(), body, StringComparison.Ordinal);
        Assert.Equal(3, database.Commands.Count);
        var page = Normalize(database.Commands[0].Text);
        Assert.Contains("((`w`.`CreatedAtUtc` > @createdAtUtc) OR ((`w`.`CreatedAtUtc` = @createdAtUtc) AND (`w`.`Id` > @id)))", page, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY `w`.`CreatedAtUtc`, `w`.`Id`\nLIMIT @p", page, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", page, StringComparison.Ordinal);
        // Comments were not asked for, so they are not read.
        Assert.DoesNotContain("WorkItemComments", page, StringComparison.Ordinal);
        var dependencies = Normalize(database.Commands[1].Text);
        Assert.Contains("FROM `WorkItemDependencies`", dependencies, StringComparison.Ordinal);
        Assert.Contains("(`w0`.`CreatedAtUtc` <= @lastCreatedAtUtc)", dependencies, StringComparison.Ordinal);
        // MySql.EntityFrameworkCore cannot translate Contains over a list of ids, and MariaDB has no LIMIT inside EXISTS/IN.
        Assert.DoesNotContain(" IN (", dependencies, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", dependencies, StringComparison.Ordinal);
        // The links of the page are bounded the same way.
        var links = Normalize(database.Commands[2].Text);
        Assert.Contains("FROM `WorkItemLinks`", links, StringComparison.Ordinal);
        Assert.Contains("(`w0`.`CreatedAtUtc` <= @lastCreatedAtUtc)", links, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN (", links, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", links, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_with_comments_limits_the_items_before_it_joins_their_comments()
    {
        var database = new FakeDatabase(lockResult: 1);
        using var host = await StartApiAsync(database);

        var response = await host.GetTestClient().GetAsync("/api/items?limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = Normalize(Assert.Single(database.Commands).Text);
        var limit = page.IndexOf("LIMIT @p", StringComparison.Ordinal);
        var join = page.IndexOf("LEFT JOIN `WorkItemComments`", StringComparison.Ordinal);
        Assert.True(limit > 0 && join > limit, page);
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n");

    // Answers the page query with one work item that has no epic; every other query gets no rows.
    private static DataTable? OneItem(DbCommand command)
    {
        var text = Normalize(command.CommandText);
        if (!text.StartsWith("SELECT `w`.`Id`, ", StringComparison.Ordinal) || !text.Contains("FROM `WorkItems` AS `w`", StringComparison.Ordinal))
        {
            return null;
        }

        var values = new Dictionary<string, object>
        {
            ["`w`.`Id`"] = ItemId.ToString(),
            ["`w`.`ProjectId`"] = Guid.Empty.ToString(),
            ["`w`.`Title`"] = "Item",
            ["`w`.`Type`"] = 0,
            ["`w`.`Status`"] = 0,
            ["`w`.`Priority`"] = 1,
            ["`w`.`Order`"] = 0,
            ["`w`.`CreatedAtUtc`"] = Created,
            ["`w`.`UpdatedAtUtc`"] = Created,
            ["`w`.`Version`"] = 1L
        };
        var table = new DataTable();
        var columns = text["SELECT ".Length..text.IndexOf('\n', StringComparison.Ordinal)].Split(", ");
        foreach (var column in columns)
        {
            table.Columns.Add(column, typeof(object));
        }

        table.Rows.Add(columns.Select(column => values.GetValueOrDefault(column, DBNull.Value)).ToArray());
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
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapKanbanWorkItemReadEndpoints());
                });
            })
            .StartAsync();
}
