using System.Data;
using System.Data.Common;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Services;
using MySql.Data.MySqlClient;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The SQL that MySql.EntityFrameworkCore writes for creating, disabling and issuing for a service identity, which the
// SQLite tests never see. The provider runs for real; FakeDatabase records each command and answers it.
public sealed class ServiceIdentityMySqlTests
{
    private static readonly Guid IdentityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task A_create_that_loses_the_race_for_the_name_is_classified_by_the_servers_duplicate_key_error()
    {
        // The name was free when looked up, and is still not visible afterwards (as in a REPEATABLE READ snapshot):
        // only the server's error 1062 on the unique index says it is taken.
        var database = new FakeDatabase(lockResult: 1, command => Normalize(command.CommandText).StartsWith("SELECT EXISTS", StringComparison.Ordinal)
            ? Table("e", false)
            : null)
        {
            NonQueryResult = 1,
            Fail = command => Normalize(command.CommandText).StartsWith("INSERT INTO `AppUsers`", StringComparison.Ordinal)
                ? ServerError("Duplicate entry 'kanban:service-identity-factory' for key 'IX_AppUsers_Issuer_Subject'", 1062)
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var created = await ServiceIdentityProvisioner.CreateAsync(dbContext, "Factory", CancellationToken.None);

        Assert.Null(created);
    }

    [Fact]
    public async Task A_disable_holds_the_identity_first_then_disables_it_and_revokes_its_tokens_in_one_transaction()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        Assert.True(await ServiceIdentityProvisioner.DisableAsync(dbContext, IdentityId, CancellationToken.None));

        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.Equal([0], database.TransactionStarts);
        Assert.Equal(
            [
                "UPDATE `AppUsers` AS `a`\nSET `DisabledAtUtc` = `a`.`DisabledAtUtc`\n" +
                "WHERE (`a`.`Id` = @identityId) AND (`a`.`Issuer` = 'kanban:service-identity')",
                "UPDATE `AppUsers` AS `a`\nSET `DisabledAtUtc` = @p,\n    `UpdatedAtUtc` = @p2\n" +
                "WHERE (`a`.`Id` = @identityId) AND `a`.`DisabledAtUtc` IS NULL",
                "UPDATE `PersonalAccessTokens` AS `p`\nSET `RevokedAtUtc` = @p\n" +
                "WHERE (`p`.`AppUserId` = @identityId) AND `p`.`RevokedAtUtc` IS NULL"
            ],
            texts);
        Assert.Equal(["COMMIT"], database.Locks);
    }

    [Fact]
    public async Task A_disable_of_no_service_identity_writes_nothing()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 0 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        Assert.False(await ServiceIdentityProvisioner.DisableAsync(dbContext, IdentityId, CancellationToken.None));

        Assert.Single(database.Commands);
        Assert.Empty(database.Locks);
    }

    [Fact]
    public async Task The_scope_check_of_a_forwarded_token_reads_whether_its_owner_is_disabled_in_the_same_statement()
    {
        var database = new FakeDatabase(lockResult: 1);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        Assert.Null(await new PersonalAccessTokenService(dbContext, TestDb.PatOptions()).GetScopeAsync(IdentityId, CancellationToken.None));

        var text = Normalize(Assert.Single(database.Commands).Text);
        Assert.Contains("`a`.`DisabledAtUtc`", text, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN `AppUsers` AS `a`", text, StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n").TrimEnd();

    private static DataTable Table(string column, object value)
    {
        var table = new DataTable();
        table.Columns.Add(column, value.GetType());
        table.Rows.Add(value);
        return table;
    }

    // MySqlException carries the server's error number only through an internal constructor.
    private static Exception ServerError(string message, int number) =>
        (Exception)Activator.CreateInstance(
            typeof(MySqlException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [message, number],
            culture: null)!;
}
