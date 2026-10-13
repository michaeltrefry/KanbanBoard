using KanbanBoard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

/// <summary>
/// Runs a check and the write that depends on it one caller at a time, where there is no row to lock.
/// SQLite: the caller's transaction is BEGIN IMMEDIATE, which admits one writer. MariaDB: a named lock, taken before
/// the caller's transaction starts so its reads see everything committed by earlier holders.
/// </summary>
public static class MariaDbNamedLock
{
    /// <summary>
    /// Runs <paramref name="action"/> while holding the lock <paramref name="name"/>, or returns
    /// <paramref name="busy"/> without running it when the lock was not free within <paramref name="timeoutSeconds"/>.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        KanbanDbContext dbContext,
        string name,
        int timeoutSeconds,
        T busy,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        // The connection is opened here so that it stays the same one from GET_LOCK to RELEASE_LOCK: left to
        // itself EF returns it to the pool at commit, and the lock would stay with the pooled connection.
        var useNamedLock = dbContext.Database.ProviderName == "MySql.EntityFrameworkCore";
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            try
            {
                // Inside the try so that RELEASE_LOCK always follows: a request aborted after the server granted
                // the lock but before the result was read must not return the pooled connection still holding it.
                if (useNamedLock)
                {
                    var acquired = await dbContext.Database
                        .SqlQuery<long?>($"SELECT CAST(GET_LOCK({name}, {timeoutSeconds}) AS SIGNED) AS `Value`")
                        .ToListAsync(cancellationToken);
                    if (acquired.Single() != 1)
                    {
                        return busy;
                    }
                }

                return await action();
            }
            finally
            {
                if (useNamedLock)
                {
                    // Not cancellable: the lock must go even when the request was aborted. Releasing a lock
                    // this connection does not hold returns 0 or NULL and changes nothing.
                    await dbContext.Database
                        .SqlQuery<long?>($"SELECT CAST(RELEASE_LOCK({name}) AS SIGNED) AS `Value`")
                        .ToListAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }
}
