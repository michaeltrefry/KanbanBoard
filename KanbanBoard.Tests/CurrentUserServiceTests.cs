using System.Security.Claims;
using KanbanBoard.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Tests;

public sealed class CurrentUserServiceTests
{
    [Fact]
    public async Task GetOrCreateCurrentUserAsync_creates_user_from_external_claims()
    {
        await using var dbContext = TestDb.CreateContext();
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreatePrincipal("https://identity.example/realms/main", "user-123", "Ada Lovelace", "ada@example.test")
            }
        };
        var service = new CurrentUserService(httpContextAccessor, dbContext);

        var user = await service.GetOrCreateCurrentUserAsync(CancellationToken.None);

        Assert.Equal("https://identity.example/realms/main", user.Issuer);
        Assert.Equal("user-123", user.Subject);
        Assert.Equal("Ada Lovelace", user.DisplayName);
        Assert.Equal("ada@example.test", user.Email);
        Assert.NotNull(user.LastSeenAtUtc);
        Assert.Single(await dbContext.AppUsers.ToListAsync());
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_updates_existing_user_metadata()
    {
        await using var dbContext = TestDb.CreateContext();
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreatePrincipal("issuer-a", "subject-a", "First Name", "first@example.test")
            }
        };
        var service = new CurrentUserService(httpContextAccessor, dbContext);
        var firstUser = await service.GetOrCreateCurrentUserAsync(CancellationToken.None);

        httpContextAccessor.HttpContext.User = CreatePrincipal("issuer-a", "subject-a", "Updated Name", "updated@example.test");
        var secondUser = await service.GetOrCreateCurrentUserAsync(CancellationToken.None);

        Assert.Equal(firstUser.Id, secondUser.Id);
        Assert.Equal("Updated Name", secondUser.DisplayName);
        Assert.Equal("updated@example.test", secondUser.Email);
        Assert.Single(await dbContext.AppUsers.ToListAsync());
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_rejects_unauthenticated_principal()
    {
        await using var dbContext = TestDb.CreateContext();
        var service = new CurrentUserService(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetOrCreateCurrentUserAsync(CancellationToken.None));
    }

    [Fact]
    public void TryGetExternalIdentity_rejects_principal_without_subject()
    {
        using var dbContext = TestDb.CreateContext();
        var service = new CurrentUserService(new HttpContextAccessor(), dbContext);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("iss", "issuer-a")],
            "oidc"));

        Assert.False(service.TryGetExternalIdentity(principal, out _));
    }

    private static ClaimsPrincipal CreatePrincipal(string issuer, string subject, string displayName, string email)
    {
        var claims = new[]
        {
            new Claim("iss", issuer),
            new Claim("sub", subject),
            new Claim("name", displayName),
            new Claim(ClaimTypes.Email, email)
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc", "name", "role"));
    }
}
