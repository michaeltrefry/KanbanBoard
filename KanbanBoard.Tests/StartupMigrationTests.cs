using System.Text.RegularExpressions;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;

namespace KanbanBoard.Tests;

public sealed class StartupMigrationTests
{
    // Nothing listens on port 1, so any attempt to reach the database fails quickly.
    private const string UnreachableConnection = "Server=127.0.0.1;Port=1;Database=kanban_test;User ID=test;Password=test;Connection Timeout=2;";

    [Fact]
    public async Task Startup_migration_is_skipped_for_non_relational_provider()
    {
        await using var dbContext = TestDb.CreateContext();

        var applied = await StartupMigrator.MigrateAsync(dbContext, new DatabaseOptions(), NullLogger.Instance, CancellationToken.None);

        Assert.Empty(applied);
    }

    [Fact]
    public async Task Startup_migration_switch_disables_migration_for_relational_provider()
    {
        await using var dbContext = CreateMySqlContext(UnreachableConnection);

        var applied = await StartupMigrator.MigrateAsync(
            dbContext, new DatabaseOptions { MigrateOnStartup = false }, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(applied);
    }

    [Fact]
    public async Task Startup_migration_failure_propagates_so_the_host_fails_fast()
    {
        await using var dbContext = CreateMySqlContext(UnreachableConnection);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            StartupMigrator.MigrateAsync(dbContext, new DatabaseOptions(), NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public void MigrateOnStartup_defaults_to_true()
    {
        Assert.True(new DatabaseOptions().MigrateOnStartup);
    }

    [Fact]
    public void Deploy_sql_history_rows_match_every_ef_migration()
    {
        // Production was bootstrapped from deploy/sql. If those scripts record different migration ids than the
        // migrations assembly, Migrate() on startup would try to re-create tables that already exist.
        using var dbContext = CreateMySqlContext(UnreachableConnection);
        var migrationIds = dbContext.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();

        var sqlDirectory = Path.GetDirectoryName(FindRepoFile("deploy/sql/001_initial_mariadb_schema.sql"))!;
        var historyIds = Directory.GetFiles(sqlDirectory, "*.sql")
            .SelectMany(file => Regex.Matches(
                    File.ReadAllText(file),
                    @"INSERT\s+(?:IGNORE\s+)?INTO\s+`__EFMigrationsHistory`[^;]*?VALUES\s*\(\s*'([^']+)'",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(match => match.Groups[1].Value))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(migrationIds);
        Assert.Equal(migrationIds, historyIds);
    }

    [Fact]
    public void Model_has_no_changes_missing_from_migrations()
    {
        // EF Core refuses to Migrate() when the model has drifted from the latest snapshot.
        using var dbContext = CreateMySqlContext(UnreachableConnection);

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Lock_interceptor_rewrites_only_the_provider_lock_statement()
    {
        using var lockCommand = new MySqlCommand(MariaDbMigrationLockInterceptor.ProviderLockSql);
        using var otherCommand = new MySqlCommand("SELECT GET_LOCK('other',-1);");

        MariaDbMigrationLockInterceptor.RewriteLockCommand(lockCommand);
        MariaDbMigrationLockInterceptor.RewriteLockCommand(otherCommand);

        Assert.Equal("SELECT GET_LOCK('__EFMigrationsLock',3600);", lockCommand.CommandText);
        Assert.Equal("SELECT GET_LOCK('other',-1);", otherCommand.CommandText);
    }

    [Fact]
    public void Lock_interceptor_widens_mariadb_int_result_to_long()
    {
        using var lockCommand = new MySqlCommand(MariaDbMigrationLockInterceptor.MariaDbLockSql);
        using var otherCommand = new MySqlCommand("SELECT 1;");

        Assert.IsType<long>(MariaDbMigrationLockInterceptor.WidenLockResult(lockCommand, 1));
        Assert.Equal(1L, MariaDbMigrationLockInterceptor.WidenLockResult(lockCommand, 1));
        Assert.IsType<int>(MariaDbMigrationLockInterceptor.WidenLockResult(otherCommand, 1));
    }

    private static KanbanDbContext CreateMySqlContext(string connectionString) =>
        new(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseMySQL(connectionString)
            .AddInterceptors(new MariaDbMigrationLockInterceptor())
            .Options);

    private static string FindRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {relativePath} above {AppContext.BaseDirectory}.");
    }
}
