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

        var options = new DbContextOptionsBuilder<KanbanDbContext>()
            .UseMySQL(connectionString)
            .Options;

        return new KanbanDbContext(options);
    }
}
