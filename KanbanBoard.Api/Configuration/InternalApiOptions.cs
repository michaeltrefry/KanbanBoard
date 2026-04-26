namespace KanbanBoard.Api.Configuration;

public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";
    public const int MinimumSharedSecretLength = 32;

    public string? SharedSecret { get; set; }

    public IReadOnlyList<string> Validate(bool required)
    {
        if (!required && string.IsNullOrWhiteSpace(SharedSecret))
        {
            return [];
        }

        return HasUsableSharedSecret()
            ? []
            : [$"InternalApi:SharedSecret must be at least {MinimumSharedSecretLength} characters when internal API authentication is enabled."];
    }

    public bool HasUsableSharedSecret() =>
        !string.IsNullOrWhiteSpace(SharedSecret) &&
        SharedSecret.Trim().Length >= MinimumSharedSecretLength;
}
