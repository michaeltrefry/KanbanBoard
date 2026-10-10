using KanbanBoard.Api.Configuration;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Data;

public static class StartupMigrator
{
    // Applies pending EF Core migrations. Returns the ids it applied (empty when skipped or up to date).
    // Any failure propagates so the host stops instead of serving against a half-migrated schema.
    public static async Task<IReadOnlyList<string>> MigrateAsync(
        KanbanDbContext dbContext,
        DatabaseOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!options.MigrateOnStartup)
        {
            logger.LogInformation("Database:MigrateOnStartup is false; skipping startup schema migration.");
            return [];
        }

        if (!dbContext.Database.IsRelational())
        {
            logger.LogInformation("Database provider is not relational; skipping startup schema migration.");
            return [];
        }

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date; no migrations to apply.");
            return [];
        }

        logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        // The normal command timeout is shorter than the migration lock wait. If the client timed out first,
        // the server would keep the lock and the provider would hang instead of throwing, so migrate under a
        // longer timeout and restore the normal one afterwards.
        var previousTimeout = dbContext.Database.GetCommandTimeout();
        dbContext.Database.SetCommandTimeout(options.MigrationCommandTimeoutSeconds);
        try
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            dbContext.Database.SetCommandTimeout(previousTimeout);
        }

        logger.LogInformation("Applied database migration(s): {Migrations}", string.Join(", ", pending));
        return pending;
    }
}
