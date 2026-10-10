using KanbanBoard.Api.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KanbanBoard.Api.Data;

public sealed class KanbanDbContextFactory : IDesignTimeDbContextFactory<KanbanDbContext>
{
    public KanbanDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Kanban")
            ?? Environment.GetEnvironmentVariable("KANBAN_MIGRATIONS_CONNECTION")
            ?? "Server=127.0.0.1;Port=3306;Database=kanban_design_time;User ID=kanban;Password=kanban;";

        // dotnet ef database update runs every command under this timeout, so it must outlast the lock wait.
        var databaseOptions = new DatabaseOptions();
        var options = new DbContextOptionsBuilder<KanbanDbContext>()
            .UseMySQL(connectionString, mySqlOptions => mySqlOptions.CommandTimeout(databaseOptions.MigrationCommandTimeoutSeconds))
            .AddInterceptors(new MariaDbMigrationLockInterceptor(databaseOptions.MigrationLockTimeoutSeconds))
            .Options;

        return new KanbanDbContext(options);
    }
}
