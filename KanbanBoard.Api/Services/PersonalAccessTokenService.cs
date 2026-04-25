using System.Security.Cryptography;
using System.Text;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Api.Services;

public interface IPersonalAccessTokenService
{
    Task<CreatedPersonalAccessToken> CreateAsync(
        AppUser user,
        string name,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken);

    Task<PersonalAccessTokenValidationResult?> ValidateAsync(
        string token,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(
        Guid appUserId,
        Guid tokenId,
        CancellationToken cancellationToken);
}

public sealed record CreatedPersonalAccessToken(
    PersonalAccessToken Token,
    string PlaintextToken);

public sealed record PersonalAccessTokenValidationResult(
    AppUser User,
    PersonalAccessToken Token);

public sealed class PersonalAccessTokenService(
    KanbanDbContext dbContext,
    IOptions<PersonalAccessTokenOptions> options) : IPersonalAccessTokenService
{
    private const int SecretByteLength = 32;
    private const int DisplayPrefixByteLength = 8;
    private const int NonceByteLength = 12;
    private const int TagByteLength = 16;
    private readonly PersonalAccessTokenOptions options = options.Value;

    public async Task<CreatedPersonalAccessToken> CreateAsync(
        AppUser user,
        string name,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();

        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("A PAT name is required.", nameof(name));
        }

        var tokenPrefix = Base64UrlEncode(RandomNumberGenerator.GetBytes(DisplayPrefixByteLength));
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretByteLength));
        var plaintextToken = $"{options.TokenPrefix}_{tokenPrefix}_{secret}";
        var now = DateTimeOffset.UtcNow;

        var token = new PersonalAccessToken
        {
            AppUserId = user.Id,
            Name = normalizedName,
            TokenPrefix = tokenPrefix,
            TokenHash = ComputeLookupHash(plaintextToken),
            EncryptedSecret = EncryptSecret(secret),
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc
        };

        dbContext.PersonalAccessTokens.Add(token);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreatedPersonalAccessToken(token, plaintextToken);
    }

    public async Task<PersonalAccessTokenValidationResult?> ValidateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();

        if (!TryParseToken(token, out _, out var secret))
        {
            return null;
        }

        var tokenHash = ComputeLookupHash(token);
        var storedToken = await dbContext.PersonalAccessTokens
            .Include(candidate => candidate.AppUser)
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);

        if (storedToken?.AppUser is null ||
            storedToken.RevokedAtUtc is not null ||
            storedToken.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var storedSecret = DecryptSecret(storedToken.EncryptedSecret);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedSecret),
            Encoding.UTF8.GetBytes(secret)))
        {
            return null;
        }

        storedToken.LastUsedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PersonalAccessTokenValidationResult(storedToken.AppUser, storedToken);
    }

    public async Task<bool> RevokeAsync(
        Guid appUserId,
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        var token = await dbContext.PersonalAccessTokens
            .FirstOrDefaultAsync(candidate => candidate.Id == tokenId && candidate.AppUserId == appUserId, cancellationToken);

        if (token is null)
        {
            return false;
        }

        token.RevokedAtUtc ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Personal access tokens are not enabled.");
        }
    }

    private bool TryParseToken(string token, out string tokenPrefix, out string secret)
    {
        tokenPrefix = string.Empty;
        secret = string.Empty;

        var parts = token.Split('_', 3);
        if (parts.Length != 3 ||
            !string.Equals(parts[0], options.TokenPrefix, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            return false;
        }

        tokenPrefix = parts[1];
        secret = parts[2];
        return true;
    }

    private string ComputeLookupHash(string token)
    {
        using var hmac = new HMACSHA256(DeriveKey("kanban-pat-lookup-v1"));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }

    private string EncryptSecret(string secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceByteLength);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagByteLength];

        using var aes = new AesGcm(DeriveKey("kanban-pat-encryption-v1"), TagByteLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return $"v1.{Base64UrlEncode(nonce)}.{Base64UrlEncode(ciphertext)}.{Base64UrlEncode(tag)}";
    }

    private string DecryptSecret(string encryptedSecret)
    {
        var parts = encryptedSecret.Split('.', 4);
        if (parts.Length != 4 || !string.Equals(parts[0], "v1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stored PAT secret is not in a supported format.");
        }

        var nonce = Base64UrlDecode(parts[1]);
        var ciphertext = Base64UrlDecode(parts[2]);
        var tag = Base64UrlDecode(parts[3]);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(DeriveKey("kanban-pat-encryption-v1"), TagByteLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] DeriveKey(string purpose)
    {
        using var hmac = new HMACSHA256(options.GetEncryptionKey());
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
