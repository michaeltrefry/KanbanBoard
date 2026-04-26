using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Tests;

public sealed class PersonalAccessTokenServiceTests
{
    [Fact]
    public async Task CreateAsync_stores_encrypted_metadata_and_returns_plaintext_once()
    {
        await using var dbContext = TestDb.CreateContext();
        var user = await AddUserAsync(dbContext);
        var service = new PersonalAccessTokenService(dbContext, TestDb.PatOptions());

        var created = await service.CreateAsync(user, "  Codex MCP  ", null, CancellationToken.None);

        Assert.StartsWith("kbp_", created.PlaintextToken, StringComparison.Ordinal);
        Assert.Equal("Codex MCP", created.Token.Name);
        Assert.NotEmpty(created.Token.TokenPrefix);
        Assert.NotEqual(created.PlaintextToken, created.Token.TokenHash);
        Assert.DoesNotContain(created.PlaintextToken, created.Token.EncryptedSecret, StringComparison.Ordinal);
        Assert.StartsWith("v1.", created.Token.EncryptedSecret, StringComparison.Ordinal);
        Assert.Null(created.Token.LastUsedAtUtc);
    }

    [Fact]
    public async Task ValidateAsync_accepts_active_token_and_updates_last_used()
    {
        await using var dbContext = TestDb.CreateContext();
        var user = await AddUserAsync(dbContext);
        var service = new PersonalAccessTokenService(dbContext, TestDb.PatOptions());
        var created = await service.CreateAsync(user, "MCP", null, CancellationToken.None);

        var result = await service.ValidateAsync(created.PlaintextToken, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.User.Id);
        Assert.Equal(created.Token.Id, result.Token.Id);
        Assert.NotNull(result.Token.LastUsedAtUtc);
    }

    [Fact]
    public async Task ValidateAsync_rejects_malformed_revoked_and_expired_tokens()
    {
        await using var dbContext = TestDb.CreateContext();
        var user = await AddUserAsync(dbContext);
        var service = new PersonalAccessTokenService(dbContext, TestDb.PatOptions());
        var created = await service.CreateAsync(user, "MCP", null, CancellationToken.None);

        Assert.Null(await service.ValidateAsync("not-a-token", CancellationToken.None));

        Assert.True(await service.RevokeAsync(user.Id, created.Token.Id, CancellationToken.None));
        Assert.Null(await service.ValidateAsync(created.PlaintextToken, CancellationToken.None));

        var expiring = await service.CreateAsync(user, "Expired", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        expiring.Token.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();

        Assert.Null(await service.ValidateAsync(expiring.PlaintextToken, CancellationToken.None));
    }

    [Fact]
    public async Task RevokeAsync_is_scoped_to_token_owner()
    {
        await using var dbContext = TestDb.CreateContext();
        var firstUser = await AddUserAsync(dbContext, subject: "user-1");
        var secondUser = await AddUserAsync(dbContext, subject: "user-2");
        var service = new PersonalAccessTokenService(dbContext, TestDb.PatOptions());
        var created = await service.CreateAsync(firstUser, "MCP", null, CancellationToken.None);

        Assert.False(await service.RevokeAsync(secondUser.Id, created.Token.Id, CancellationToken.None));
        Assert.Null(created.Token.RevokedAtUtc);

        Assert.True(await service.RevokeAsync(firstUser.Id, created.Token.Id, CancellationToken.None));
        Assert.NotNull(created.Token.RevokedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AQID")]
    public void PersonalAccessTokenOptions_rejects_missing_or_malformed_enabled_key(string? encryptionKey)
    {
        var options = new PersonalAccessTokenOptions
        {
            Enabled = true,
            EncryptionKey = encryptionKey
        };

        Assert.Contains(options.Validate(), error => error.Contains("32-byte / 256-bit key", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => options.GetEncryptionKey());
    }

    [Fact]
    public void PersonalAccessTokenOptions_accepts_base64_and_hex_32_byte_keys()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var base64Options = new PersonalAccessTokenOptions
        {
            Enabled = true,
            EncryptionKey = Convert.ToBase64String(key)
        };
        var hexOptions = new PersonalAccessTokenOptions
        {
            Enabled = true,
            EncryptionKey = Convert.ToHexString(key)
        };

        Assert.Empty(base64Options.Validate());
        Assert.Empty(hexOptions.Validate());
        Assert.Equal(key, base64Options.GetEncryptionKey());
        Assert.Equal(key, hexOptions.GetEncryptionKey());
    }

    private static async Task<AppUser> AddUserAsync(
        DbContext dbContext,
        string issuer = "issuer",
        string subject = "subject")
    {
        var user = new AppUser
        {
            Issuer = issuer,
            Subject = subject,
            DisplayName = subject,
            Email = $"{subject}@example.test"
        };

        dbContext.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }
}
