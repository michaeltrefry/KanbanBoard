using System.Data;
using System.Data.Common;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data.Migrations;
using KanbanBoard.Api.Services;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
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
        var database = new FakeDatabase(lockResult: 1, NamedLockGranted)
        {
            NonQueryResult = 1,
            Fail = command => Normalize(command.CommandText).StartsWith("INSERT INTO `AppUsers`", StringComparison.Ordinal)
                ? ServerError("Duplicate entry 'kanban:service-identity-factory' for key 'IX_AppUsers_Issuer_Subject'", 1062)
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var created = await ServiceIdentityProvisioner.CreateAsync(dbContext, "Factory", CancellationToken.None);

        Assert.Equal(ServiceIdentityCreateOutcome.Exists, created.Outcome);
    }

    [Fact]
    public async Task A_create_looks_the_name_up_and_inserts_it_under_the_create_lock()
    {
        var database = new FakeDatabase(lockResult: 1, NamedLockGranted) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await Record.ExceptionAsync(() => ServiceIdentityProvisioner.CreateAsync(dbContext, "Factory", CancellationToken.None));

        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count >= 4, string.Join("\n---\n", texts));
        Assert.Equal($"GET {ServiceIdentityProvisioner.CreateLockName}", database.Locks[0]);
        Assert.StartsWith("SELECT", texts[1], StringComparison.Ordinal);
        Assert.Contains("FROM `AppUsers`", texts[1], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO `AppUsers`", texts[2], StringComparison.Ordinal);
        Assert.Equal($"RELEASE {ServiceIdentityProvisioner.CreateLockName}", database.Locks[^1]);
    }

    // The order that keeps a token from being issued for a disabled identity: after BEGIN, the identity's row is held
    // before its DisabledAtUtc is read, so the read sees a disable committed before the hold.
    [Fact]
    public async Task An_issue_from_the_admin_page_holds_the_identity_before_it_reads_whether_it_is_disabled()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await ServiceIdentityProvisioner.IssueTokenAsync(dbContext, new PersonalAccessTokenService(dbContext, TestDb.PatOptions()),
            IdentityId, "Token", null, "full", null, CancellationToken.None);

        AssertHoldsTheIdentityByIdBeforeReadingIt(database);
    }

    [Fact]
    public async Task A_rotate_holds_the_identity_before_it_reads_whether_it_is_disabled()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await ServiceIdentityProvisioner.RotateTokenAsync(dbContext, new PersonalAccessTokenService(dbContext, TestDb.PatOptions()),
            IdentityId, Guid.NewGuid(), null, null, CancellationToken.None);

        AssertHoldsTheIdentityByIdBeforeReadingIt(database);
    }

    [Fact]
    public async Task A_revoke_holds_the_identity_first_so_it_runs_after_or_before_a_rotate_of_the_token()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        Assert.False(await ServiceIdentityProvisioner.RevokeTokenAsync(dbContext, IdentityId, Guid.NewGuid(), null, DateTimeOffset.UtcNow, CancellationToken.None));

        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 2, string.Join("\n---\n", texts));
        Assert.Equal([0], database.TransactionStarts);
        Assert.Equal(
            "UPDATE `AppUsers` AS `a`\nSET `DisabledAtUtc` = `a`.`DisabledAtUtc`\n" +
            "WHERE (`a`.`Id` = @identityId) AND (`a`.`Issuer` = 'kanban:service-identity')",
            texts[0]);
        Assert.Contains("FROM `PersonalAccessTokens`", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_holds_the_identity_before_it_reads_whether_it_is_disabled()
    {
        var database = new FakeDatabase(lockResult: 1, NamedLockGranted) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        // Whatever the fake answers to the inserts that follow; only the first statements matter here.
        await Record.ExceptionAsync(() => ServiceIdentityProvisioner.ProvisionAsync(dbContext,
            new PersonalAccessTokenService(dbContext, TestDb.PatOptions()), "Factory", rotate: true, CancellationToken.None));

        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        var begin = database.TransactionStarts[0];
        Assert.True(texts.Count > begin + 1, string.Join("\n---\n", texts));
        // Under the create lock, taken before the transaction.
        Assert.Equal($"GET {ServiceIdentityProvisioner.CreateLockName}", database.Locks[0]);
        Assert.Contains("GET_LOCK(", texts[begin - 1], StringComparison.Ordinal);
        Assert.Equal(
            "UPDATE `AppUsers` AS `a`\nSET `DisabledAtUtc` = `a`.`DisabledAtUtc`\n" +
            "WHERE (`a`.`Issuer` = 'kanban:service-identity') AND (`a`.`Subject` = @subject)",
            texts[begin]);
        Assert.StartsWith("SELECT", texts[begin + 1], StringComparison.Ordinal);
        Assert.Contains("FROM `AppUsers`", texts[begin + 1], StringComparison.Ordinal);
    }

    private static void AssertHoldsTheIdentityByIdBeforeReadingIt(FakeDatabase database)
    {
        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count >= 2, string.Join("\n---\n", texts));
        Assert.Equal([0], database.TransactionStarts);
        Assert.Equal(
            "UPDATE `AppUsers` AS `a`\nSET `DisabledAtUtc` = `a`.`DisabledAtUtc`\n" +
            "WHERE (`a`.`Id` = @identityId) AND (`a`.`Issuer` = 'kanban:service-identity')",
            texts[0]);
        Assert.StartsWith("SELECT", texts[1], StringComparison.Ordinal);
        Assert.Contains("FROM `AppUsers`", texts[1], StringComparison.Ordinal);
    }

    // GET_LOCK and RELEASE_LOCK of the create lock answer 1; every other query finds no rows.
    private static DataTable? NamedLockGranted(DbCommand command) =>
        command.CommandText.Contains("_LOCK(", StringComparison.Ordinal) ? Table("Value", 1L) : null;

    [Fact]
    public async Task A_disable_holds_the_identity_first_then_disables_it_and_revokes_its_tokens_in_one_transaction()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        Assert.True(await ServiceIdentityProvisioner.DisableAsync(dbContext, IdentityId, new ChangeActor(Guid.NewGuid(), "Ada"), DateTimeOffset.UtcNow, CancellationToken.None));

        var texts = database.Commands.Select(command => Normalize(command.Text)).ToList();
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.Equal([0], database.TransactionStarts);
        Assert.Equal(
            [
                "UPDATE `AppUsers` AS `a`\nSET `DisabledAtUtc` = `a`.`DisabledAtUtc`\n" +
                "WHERE (`a`.`Id` = @identityId) AND (`a`.`Issuer` = 'kanban:service-identity')",
                "UPDATE `AppUsers` AS `a`\nSET `DisabledAtUtc` = @p,\n    `UpdatedAtUtc` = @p2\n" +
                "WHERE (`a`.`Id` = @identityId) AND `a`.`DisabledAtUtc` IS NULL"
            ],
            texts[..2]);
        // The admin is recorded only on the tokens this disable revokes: one revoked before keeps its revoker.
        Assert.Matches(
            @"^UPDATE `PersonalAccessTokens` AS `p`\nSET `RevokedAtUtc` = @\w+,\n    `RevokedByUserId` = @\w+,\n    `RevokedByName` = @\w+\n" +
            @"WHERE \(`p`\.`AppUserId` = @identityId\) AND `p`\.`RevokedAtUtc` IS NULL$",
            texts[2]);
        Assert.Equal(["COMMIT"], database.Locks);
    }

    [Fact]
    public async Task A_disable_of_no_service_identity_writes_nothing()
    {
        var database = new FakeDatabase(lockResult: 1) { NonQueryResult = 0 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        Assert.False(await ServiceIdentityProvisioner.DisableAsync(dbContext, IdentityId, null, DateTimeOffset.UtcNow, CancellationToken.None));

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

    [Fact]
    public void Migration_and_deploy_script_add_the_same_nullable_admin_columns_and_nothing_else()
    {
        var migration = new AddPersonalAccessTokenAdmins();
        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToList();
        var script = File.ReadAllText(FindRepoFile("deploy/sql/012_personal_access_token_admins.sql")).ReplaceLineEndings("\n");

        // Existing tokens keep their rows: every new column is nullable and nothing is altered, dropped or keyed.
        Assert.Equal(4, migration.UpOperations.Count);
        Assert.Equal(
            [("IssuedByName", "varchar(320)"), ("IssuedByUserId", "varchar(36)"), ("RevokedByName", "varchar(320)"), ("RevokedByUserId", "varchar(36)")],
            columns.Select(column => (column.Name, column.ColumnType!)).ToArray());
        var positions = columns.Select(column =>
        {
            Assert.Equal(("PersonalAccessTokens", true), (column.Table, column.IsNullable));
            var line = $"ALTER TABLE `PersonalAccessTokens` ADD COLUMN IF NOT EXISTS `{column.Name}` {column.ColumnType} NULL;";
            Assert.Contains(line, script, StringComparison.Ordinal);
            return script.IndexOf(line, StringComparison.Ordinal);
        }).ToList();
        // The same order as the migration, so both give the same table.
        Assert.Equal(positions.Order(), positions);
        Assert.Contains($"VALUES ('{typeof(AddPersonalAccessTokenAdmins).GetCustomAttributes(typeof(Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute), false)
            .Cast<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>().Single().Id}', '10.0.6');", script, StringComparison.Ordinal);
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
