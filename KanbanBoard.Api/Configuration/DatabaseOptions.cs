namespace KanbanBoard.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const int MigrationLockMarginSeconds = 60;

    public int CommandTimeoutSeconds { get; set; } = 30;

    // Command timeout for startup migration only. It must outlast the GET_LOCK wait on the migration lock:
    // if the client gives up first, the server keeps holding the lock and the provider never surfaces an error.
    public int MigrationCommandTimeoutSeconds { get; set; } = 660;

    // How long startup migration waits for another instance's migration lock before it fails.
    // Derived so it always ends before MigrationCommandTimeoutSeconds.
    public int MigrationLockTimeoutSeconds => MigrationCommandTimeoutSeconds - MigrationLockMarginSeconds;

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

        if (MigrationCommandTimeoutSeconds is < 90 or > 7200)
        {
            errors.Add("Database:MigrationCommandTimeoutSeconds must be between 90 and 7200.");
        }

        return errors;
    }
}
