namespace KanbanBoard.Api.Configuration;

public sealed class KanbanAuthOptions
{
    public const string SectionName = "Auth";

    public bool Enabled { get; set; }
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Audience { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";
    public string CookieName { get; set; } = "KanbanBoard.Auth";
    public bool RequireHttpsMetadata { get; set; } = true;
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public IReadOnlyList<string> Validate()
    {
        if (!Enabled)
        {
            return [];
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Authority))
        {
            errors.Add("Auth:Authority is required when Auth:Enabled is true.");
        }
        else if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authorityUri) ||
            !authorityUri.IsAbsoluteUri ||
            string.IsNullOrWhiteSpace(authorityUri.Scheme) ||
            string.IsNullOrWhiteSpace(authorityUri.Host))
        {
            errors.Add("Auth:Authority must be an absolute URL when Auth:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            errors.Add("Auth:ClientId is required when Auth:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            errors.Add("Auth:ClientSecret is required when Auth:Enabled is true.");
        }

        if (!IsRootedPath(CallbackPath))
        {
            errors.Add("Auth:CallbackPath must start with '/'.");
        }

        if (!IsRootedPath(SignedOutCallbackPath))
        {
            errors.Add("Auth:SignedOutCallbackPath must start with '/'.");
        }

        if (string.IsNullOrWhiteSpace(CookieName))
        {
            errors.Add("Auth:CookieName is required when Auth:Enabled is true.");
        }

        if (Scopes is null || Scopes.Length == 0 || !Scopes.Any(scope => string.Equals(scope, "openid", StringComparison.Ordinal)))
        {
            errors.Add("Auth:Scopes must include the 'openid' scope.");
        }

        return errors;
    }

    private static bool IsRootedPath(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.StartsWith("/", StringComparison.Ordinal);
    }
}
