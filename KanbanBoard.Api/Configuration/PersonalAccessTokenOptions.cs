using System.Globalization;

namespace KanbanBoard.Api.Configuration;

public sealed class PersonalAccessTokenOptions
{
    public const string SectionName = "PersonalAccessTokens";
    public const int RequiredKeySizeBytes = 32;

    public bool Enabled { get; set; }
    public string? EncryptionKey { get; set; }
    public string TokenPrefix { get; set; } = "kbp";

    public IReadOnlyList<string> Validate()
    {
        if (!Enabled)
        {
            return [];
        }

        var errors = new List<string>();

        if (!TryDecodeEncryptionKey(EncryptionKey, out _))
        {
            errors.Add("PersonalAccessTokens:EncryptionKey must be a 32-byte / 256-bit key encoded as base64 or 64 hex characters when PersonalAccessTokens:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(TokenPrefix))
        {
            errors.Add("PersonalAccessTokens:TokenPrefix is required when PersonalAccessTokens:Enabled is true.");
        }

        return errors;
    }

    public byte[] GetEncryptionKey()
    {
        if (!TryDecodeEncryptionKey(EncryptionKey, out var key))
        {
            throw new InvalidOperationException("PersonalAccessTokens:EncryptionKey must be a 32-byte / 256-bit key encoded as base64 or 64 hex characters.");
        }

        return key;
    }

    public static bool TryDecodeEncryptionKey(string? value, out byte[] key)
    {
        key = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length == RequiredKeySizeBytes * 2 && normalized.All(Uri.IsHexDigit))
        {
            key = new byte[RequiredKeySizeBytes];
            for (var index = 0; index < key.Length; index++)
            {
                key[index] = byte.Parse(normalized.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return true;
        }

        try
        {
            key = Convert.FromBase64String(normalized);
            return key.Length == RequiredKeySizeBytes;
        }
        catch (FormatException)
        {
            key = [];
            return false;
        }
    }
}
