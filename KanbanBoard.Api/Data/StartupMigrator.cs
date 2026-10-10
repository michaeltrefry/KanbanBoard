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
        await dbContext.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Applied database migration(s): {Migrations}", string.Join(", ", pending));
        return pending;
    }
}
