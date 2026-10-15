namespace KanbanBoard.Api.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "App";

    // The address people open the board at, such as https://kanban.example.com, from which the url of a story's page
    // is made. It is configured rather than taken from the request: behind the proxy, and for the MCP host that calls
    // the API on the internal network, the request names a host nobody can open. Unset: stories have no url.
    public string? PublicBaseUrl { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var baseUrl = NormalizedBaseUrl();
        if (baseUrl is null)
        {
            return [];
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment)
                ? []
                : ["App:PublicBaseUrl must be an absolute http or https URL without a query or a fragment, such as https://kanban.example.com."];
    }

    /// <summary>The page of the story, or null when <see cref="PublicBaseUrl"/> is not set.</summary>
    public string? StoryUrl(Guid id) =>
        NormalizedBaseUrl() is { } baseUrl ? $"{baseUrl}/stories/{id}" : null;

    // Without the trailing slashes, so that a base of https://host/ does not make https://host//stories.
    private string? NormalizedBaseUrl() =>
        string.IsNullOrWhiteSpace(PublicBaseUrl) ? null : PublicBaseUrl.Trim().TrimEnd('/');
}
