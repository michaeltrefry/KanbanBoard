namespace KanbanBoard.Mcp.Configuration;

public sealed class McpAuthenticationOptions
{
    public const string SectionName = "McpAuthentication";

    public bool RequirePersonalAccessToken { get; set; } = true;
    public int ValidationCacheSeconds { get; set; } = 30;

    public IReadOnlyList<string> Validate()
    {
        if (!RequirePersonalAccessToken)
        {
            return [];
        }

        return ValidationCacheSeconds is >= 1 and <= 300
            ? []
            : ["McpAuthentication:ValidationCacheSeconds must be between 1 and 300 seconds."];
    }
}
