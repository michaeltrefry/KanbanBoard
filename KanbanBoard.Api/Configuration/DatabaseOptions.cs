namespace KanbanBoard.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public int CommandTimeoutSeconds { get; set; } = 30;

    // Apply pending EF Core migrations when the API starts. Turn off to manage the schema by hand with deploy/sql.
    public bool MigrateOnStartup { get; set; } = true;

    public IReadOnlyList<string> Validate(string? connectionString)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("ConnectionStrings:Kanban is required and must point to a MariaDB database.");
        }

        if (CommandTimeoutSeconds is < 1 or > 600)
        {
            errors.Add("Database:CommandTimeoutSeconds must be between 1 and 600.");
        }

        return errors;
    }
}
