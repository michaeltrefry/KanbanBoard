using System.Security.Claims;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public interface ICurrentUserService
{
    Task<AppUser> GetOrCreateCurrentUserAsync(CancellationToken cancellationToken);
    bool TryGetExternalIdentity(ClaimsPrincipal principal, out ExternalUserIdentity identity);
}

public sealed record ExternalUserIdentity(
    string Issuer,
    string Subject,
    string? DisplayName,
    string? Email);

public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor, KanbanDbContext dbContext) : ICurrentUserService
{
    public async Task<AppUser> GetOrCreateCurrentUserAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is null || !TryGetExternalIdentity(principal, out var identity))
        {
            throw new InvalidOperationException("The current request does not contain an authenticated external user.");
        }

        var now = DateTimeOffset.UtcNow;
        var user = await dbContext.AppUsers
            .FirstOrDefaultAsync(
                candidate => candidate.Issuer == identity.Issuer && candidate.Subject == identity.Subject,
                cancellationToken);

        if (user is null)
        {
            user = new AppUser
            {
                Issuer = identity.Issuer,
                Subject = identity.Subject,
                DisplayName = identity.DisplayName,
                Email = identity.Email,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastSeenAtUtc = now
            };

            dbContext.AppUsers.Add(user);
        }
        else
        {
            user.DisplayName = identity.DisplayName;
            user.Email = identity.Email;
            user.UpdatedAtUtc = now;
            user.LastSeenAtUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return user;
    }

    public bool TryGetExternalIdentity(ClaimsPrincipal principal, out ExternalUserIdentity identity)
    {
        identity = default!;

        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var subjectClaim = principal.FindFirst("sub") ?? principal.FindFirst(ClaimTypes.NameIdentifier);
        if (subjectClaim is null || string.IsNullOrWhiteSpace(subjectClaim.Value))
        {
            return false;
        }

        var issuer = NormalizeClaimValue(principal.FindFirst("iss")?.Value)
            ?? NormalizeClaimValue(subjectClaim.Issuer);

        if (issuer is null)
        {
            return false;
        }

        var subject = subjectClaim.Value.Trim();
        identity = new ExternalUserIdentity(
            issuer,
            subject,
            NormalizeClaimValue(principal.FindFirst("name")?.Value) ?? NormalizeClaimValue(principal.Identity.Name),
            NormalizeClaimValue(principal.FindFirst(ClaimTypes.Email)?.Value) ?? NormalizeClaimValue(principal.FindFirst("email")?.Value));

        return true;
    }

    private static string? NormalizeClaimValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
