using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
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
        var interceptor = new MariaDbMigrationLockInterceptor(600);
        using var lockCommand = new MySqlCommand(MariaDbMigrationLockInterceptor.ProviderLockSql);
        using var otherCommand = new MySqlCommand("SELECT GET_LOCK('other',-1);");

        interceptor.RewriteLockCommand(lockCommand);
        interceptor.RewriteLockCommand(otherCommand);

        Assert.Equal("SELECT GET_LOCK('__EFMigrationsLock',600);", lockCommand.CommandText);
        Assert.Equal("SELECT GET_LOCK('other',-1);", otherCommand.CommandText);
    }

    [Fact]
    public void Lock_interceptor_widens_mariadb_int_result_to_long()
    {
        var interceptor = new MariaDbMigrationLockInterceptor(600);
        using var lockCommand = new MySqlCommand(interceptor.MariaDbLockSql);
        using var otherCommand = new MySqlCommand("SELECT 1;");

        Assert.IsType<long>(interceptor.WidenLockResult(lockCommand, 1));
        Assert.Equal(1L, interceptor.WidenLockResult(lockCommand, 1));
        Assert.IsType<int>(interceptor.WidenLockResult(otherCommand, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void Lock_interceptor_throws_when_the_lock_was_not_acquired(int? lockResult)
    {
        var interceptor = new MariaDbMigrationLockInterceptor(600);
        using var lockCommand = new MySqlCommand(interceptor.MariaDbLockSql);

        Assert.Throws<TimeoutException>(() => interceptor.WidenLockResult(lockCommand, lockResult));
    }

    [Fact]
    public void Migration_lock_wait_always_ends_before_the_migration_command_timeout()
    {
        // If the client-side command timeout fires first, MariaDB keeps the lock and the provider hangs.
        var defaults = new DatabaseOptions();
        Assert.Equal(600, defaults.MigrationLockTimeoutSeconds);
        Assert.True(defaults.MigrationCommandTimeoutSeconds >= defaults.MigrationLockTimeoutSeconds + 30);

        for (var seconds = 1; seconds <= 10_000; seconds++)
        {
            var options = new DatabaseOptions { MigrationCommandTimeoutSeconds = seconds };
            if (options.Validate("Server=x").Count == 0)
            {
                Assert.InRange(options.MigrationLockTimeoutSeconds, 1, options.MigrationCommandTimeoutSeconds - 30);
            }
        }

        Assert.NotEmpty(new DatabaseOptions { MigrationCommandTimeoutSeconds = 89 }.Validate("Server=x"));
        Assert.NotEmpty(new DatabaseOptions { MigrationCommandTimeoutSeconds = 7201 }.Validate("Server=x"));
    }

    [Fact]
    public async Task Startup_migration_runs_under_the_migration_timeout_and_restores_the_normal_one()
    {
        var options = new DatabaseOptions { CommandTimeoutSeconds = 30, MigrationCommandTimeoutSeconds = 200 };
        var database = new FakeDatabase(lockResult: 1);
        await using var dbContext = CreatePipelineContext(options, database);

        var applied = await StartupMigrator.MigrateAsync(dbContext, options, NullLogger.Instance, CancellationToken.None);

        Assert.NotEmpty(applied);
        var lockCommands = database.Commands.Where(command => command.Text.Contains("GET_LOCK('__EFMigrationsLock'")).ToList();
        Assert.NotEmpty(lockCommands);
        Assert.All(lockCommands, command =>
        {
            Assert.Equal("SELECT GET_LOCK('__EFMigrationsLock',140);", command.Text);
            Assert.Equal(200, command.Timeout);
        });
        Assert.Equal(30, dbContext.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task Lock_interceptor_rewrites_the_lock_statement_through_the_async_ef_pipeline()
    {
        var database = new FakeDatabase(lockResult: 1);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await dbContext.Database.MigrateAsync();

        Assert.Contains(database.Commands, command => command.Text == "SELECT GET_LOCK('__EFMigrationsLock',600);");
        Assert.DoesNotContain(database.Commands, command => command.Text == MariaDbMigrationLockInterceptor.ProviderLockSql);
        Assert.Contains(database.Commands, command => command.Text.Contains("CREATE TABLE `AppUsers`"));
    }

    [Fact]
    public void Lock_interceptor_rewrites_the_lock_statement_through_the_sync_ef_pipeline()
    {
        var database = new FakeDatabase(lockResult: 1);
        using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        dbContext.Database.Migrate();

        Assert.Contains(database.Commands, command => command.Text == "SELECT GET_LOCK('__EFMigrationsLock',600);");
        Assert.DoesNotContain(database.Commands, command => command.Text == MariaDbMigrationLockInterceptor.ProviderLockSql);
        Assert.Contains(database.Commands, command => command.Text.Contains("CREATE TABLE `AppUsers`"));
    }

    [Fact]
    public async Task Lock_wait_timeout_fails_async_migration_before_any_schema_change()
    {
        var database = new FakeDatabase(lockResult: 0);
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        await Assert.ThrowsAsync<TimeoutException>(() => dbContext.Database.MigrateAsync());

        Assert.DoesNotContain(database.Commands, command => command.Text.Contains("CREATE TABLE `AppUsers`"));
    }

    [Fact]
    public void Lock_wait_timeout_fails_sync_migration_before_any_schema_change()
    {
        var database = new FakeDatabase(lockResult: 0);
        using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        Assert.Throws<TimeoutException>(() => dbContext.Database.Migrate());

        Assert.DoesNotContain(database.Commands, command => command.Text.Contains("CREATE TABLE `AppUsers`"));
    }

    private static KanbanDbContext CreateMySqlContext(string connectionString) =>
        new(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseMySQL(connectionString)
            .AddInterceptors(new MariaDbMigrationLockInterceptor(new DatabaseOptions().MigrationLockTimeoutSeconds))
            .Options);

    // Same provider and interceptor registration as Program.cs, but every connection, command and transaction
    // is answered by FakeDatabase, so the real EF Core / MySql.EntityFrameworkCore pipeline runs without a server.
    private static KanbanDbContext CreatePipelineContext(DatabaseOptions options, FakeDatabase database) =>
        new(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseMySQL(UnreachableConnection, mySqlOptions => mySqlOptions.CommandTimeout(options.CommandTimeoutSeconds))
            .AddInterceptors(new MariaDbMigrationLockInterceptor(options.MigrationLockTimeoutSeconds), database)
            .ReplaceService<IDatabaseCreator, ExistingDatabaseCreator>()
            .ReplaceService<IRelationalDatabaseCreator, ExistingDatabaseCreator>()
            .Options);

    // MySQLDatabaseCreator.Exists opens its own raw connection, outside the interceptor pipeline.
    private sealed class ExistingDatabaseCreator(RelationalDatabaseCreatorDependencies dependencies)
        : RelationalDatabaseCreator(dependencies)
    {
        public override bool Exists() => true;
        public override bool HasTables() => true;
        public override Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public override Task<bool> HasTablesAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public override void Create() => throw new NotSupportedException();
        public override void Delete() => throw new NotSupportedException();
    }

    private sealed class FakeDatabase(int lockResult) : IDbConnectionInterceptor, IDbCommandInterceptor, IDbTransactionInterceptor
    {
        public List<(string Text, int Timeout)> Commands { get; } = [];

        private void Record(DbCommand command) => Commands.Add((command.CommandText, command.CommandTimeout));

        private object? Scalar(DbCommand command) =>
            command.CommandText.Contains("GET_LOCK('__EFMigrationsLock'", StringComparison.Ordinal) ? lockResult : null;

        private static DbDataReader EmptyHistory()
        {
            var table = new DataTable();
            table.Columns.Add("MigrationId", typeof(string));
            table.Columns.Add("ProductVersion", typeof(string));
            return table.CreateDataReader();
        }

        public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return InterceptionResult<object>.SuppressWithResult(Scalar(command)!);
        }

        public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(InterceptionResult<object>.SuppressWithResult(Scalar(command)!));
        }

        public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return InterceptionResult<int>.SuppressWithResult(0);
        }

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }

        public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return InterceptionResult<DbDataReader>.SuppressWithResult(EmptyHistory());
        }

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(EmptyHistory()));
        }

        // MySqlCommand only accepts a MySqlTransaction, whose constructor is internal. Commit and rollback are
        // suppressed below, so this transaction never touches the (closed) connection.
        private static DbTransaction BeginFakeTransaction(DbConnection connection) =>
            (DbTransaction)Activator.CreateInstance(
                typeof(MySqlTransaction),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: [connection, IsolationLevel.Unspecified],
                culture: null)!;

        public InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result) =>
            InterceptionResult<DbTransaction>.SuppressWithResult(BeginFakeTransaction(connection));

        public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<DbTransaction>.SuppressWithResult(BeginFakeTransaction(connection)));

        public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

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
