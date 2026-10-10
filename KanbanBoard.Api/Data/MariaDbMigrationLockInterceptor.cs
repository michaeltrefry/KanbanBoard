using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanBoard.Api.Data;

// MySql.EntityFrameworkCore takes the migration lock with SELECT GET_LOCK('__EFMigrationsLock',-1)
// and casts the result to long. Both parts break on MariaDB:
// - MySQL treats a negative timeout as "wait forever"; MariaDB rejects it (warning 1411) and returns NULL.
// - MariaDB returns GET_LOCK as INT, not BIGINT, so the provider's cast to long throws.
// Either way Database.Migrate() fails before it applies anything. This interceptor rewrites only
// that statement to a positive timeout and widens its result to long. Every other command is untouched.
public sealed class MariaDbMigrationLockInterceptor : DbCommandInterceptor
{
    public const string ProviderLockSql = "SELECT GET_LOCK('__EFMigrationsLock',-1);";
    public const int LockTimeoutSeconds = 3600;
    public static readonly string MariaDbLockSql = $"SELECT GET_LOCK('__EFMigrationsLock',{LockTimeoutSeconds});";

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

    public static void RewriteLockCommand(DbCommand command)
    {
        if (string.Equals(command.CommandText.Trim(), ProviderLockSql, StringComparison.Ordinal))
        {
            command.CommandText = MariaDbLockSql;
        }
    }

    public static object? WidenLockResult(DbCommand command, object? result) =>
        result is int value && string.Equals(command.CommandText, MariaDbLockSql, StringComparison.Ordinal)
            ? (long)value
            : result;
}
