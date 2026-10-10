using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanBoard.Api.Data;

// MySql.EntityFrameworkCore takes the migration lock with SELECT GET_LOCK('__EFMigrationsLock',-1)
// and casts the result to long. Both parts break on MariaDB:
// - MySQL treats a negative timeout as "wait forever"; MariaDB rejects it (warning 1411) and returns NULL.
// - MariaDB returns GET_LOCK as INT, not BIGINT, so the provider's cast to long throws.
// Either way Database.Migrate() fails before it applies anything. This interceptor rewrites only
// that statement to a positive timeout and widens its result to long. Every other command is untouched.
// The provider also ignores the GET_LOCK result: when the wait times out (0) it migrates anyway, while
// another session still holds the lock. So any result other than 1 throws instead.
// The lock timeout must be lower than the command timeout the migration runs under (see StartupMigrator).
public sealed class MariaDbMigrationLockInterceptor(int lockTimeoutSeconds) : DbCommandInterceptor
{
    public const string ProviderLockSql = "SELECT GET_LOCK('__EFMigrationsLock',-1);";

    public int LockTimeoutSeconds { get; } = lockTimeoutSeconds > 0
        ? lockTimeoutSeconds
        : throw new ArgumentOutOfRangeException(nameof(lockTimeoutSeconds), "The migration lock timeout must be positive.");

    public string MariaDbLockSql => $"SELECT GET_LOCK('__EFMigrationsLock',{LockTimeoutSeconds});";

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        RewriteLockCommand(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        RewriteLockCommand(command);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result) =>
        WidenLockResult(command, result);

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(WidenLockResult(command, result));

    public void RewriteLockCommand(DbCommand command)
    {
        if (string.Equals(command.CommandText.Trim(), ProviderLockSql, StringComparison.Ordinal))
        {
            command.CommandText = MariaDbLockSql;
        }
    }

    public object? WidenLockResult(DbCommand command, object? result)
    {
        if (!string.Equals(command.CommandText, MariaDbLockSql, StringComparison.Ordinal))
        {
            return result;
        }

        var acquired = result switch
        {
            int value => (long)value,
            long value => value,
            _ => (long?)null,
        };

        if (acquired != 1)
        {
            throw new TimeoutException(
                $"Could not acquire the '__EFMigrationsLock' migration lock within {LockTimeoutSeconds} s " +
                $"(GET_LOCK returned {(acquired?.ToString() ?? "NULL")}). Another instance may be migrating, or a " +
                "stale session holds the lock; check SELECT IS_USED_LOCK('__EFMigrationsLock').");
        }

        return acquired.Value;
    }
}
