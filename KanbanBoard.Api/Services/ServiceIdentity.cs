using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public sealed record ProvisionedServiceIdentity(
    AppUser User,
    bool Created,
    int RevokedTokens,
    CreatedPersonalAccessToken FullToken,
    CreatedPersonalAccessToken ReadToken);

/// <summary>
/// A service identity is an <see cref="AppUser"/> that no person signs in as: it has no OIDC subject, only tokens.
/// </summary>
public static class ServiceIdentityProvisioner
{
    // Not a URL, so it can never equal the issuer of an OIDC login.
    public const string Issuer = "kanban:service-identity";
    public const int MaxNameLength = 120;

    public static async Task<ProvisionedServiceIdentity> ProvisionAsync(
        KanbanDbContext dbContext,
        IPersonalAccessTokenService personalAccessTokenService,
        string name,
        bool rotate,
        CancellationToken cancellationToken)
    {
        var displayName = name.Trim();
        if (displayName.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException($"A service identity name is required and must be at most {MaxNameLength} characters.", nameof(name));
        }

        var subject = displayName.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var user = await dbContext.AppUsers
            .FirstOrDefaultAsync(candidate => candidate.Issuer == Issuer && candidate.Subject == subject, cancellationToken);
        var created = user is null;
        if (user is null)
        {
            user = new AppUser { Issuer = Issuer, Subject = subject, CreatedAtUtc = now };
            dbContext.AppUsers.Add(user);
        }

        user.DisplayName = displayName;
        user.UpdatedAtUtc = now;

        var revoked = 0;
        if (rotate && !created)
        {
            var activeTokens = await dbContext.PersonalAccessTokens
                .Where(token => token.AppUserId == user.Id && token.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var token in activeTokens)
            {
                token.RevokedAtUtc = now;
            }

            revoked = activeTokens.Count;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var fullToken = await personalAccessTokenService.CreateAsync(
            user, $"{displayName} ({PersonalAccessTokenScopes.Full})", null, PersonalAccessTokenScopes.Full, cancellationToken);
        var readToken = await personalAccessTokenService.CreateAsync(
            user, $"{displayName} ({PersonalAccessTokenScopes.Read})", null, PersonalAccessTokenScopes.Read, cancellationToken);

        return new ProvisionedServiceIdentity(user, created, revoked, fullToken, readToken);
    }
}

/// <summary>
/// <c>dotnet KanbanBoard.Api.dll create-service-identity --name NAME [--rotate]</c>: creates (or reuses) a service
/// identity and prints a new full-scope and read-scope token for it. No browser or OIDC login is involved.
/// </summary>
public static class ServiceIdentityCommand
{
    public const string CommandName = "create-service-identity";

    public const string Usage =
        "Usage: dotnet KanbanBoard.Api.dll " + CommandName + " --name NAME [--rotate]\n" +
        "  --name NAME  Display name of the service identity. Re-using a name adds tokens to that identity.\n" +
        "  --rotate     Revoke the identity's existing tokens before issuing the new pair.";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? name = null;
        var rotate = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--name" && index + 1 < args.Length)
            {
                name = args[++index];
            }
            else if (args[index] == "--rotate")
            {
                rotate = true;
            }
            else
            {
                await error.WriteLineAsync($"Unrecognized argument '{args[index]}'.\n{Usage}");
                return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        ProvisionedServiceIdentity identity;
        try
        {
            identity = await ServiceIdentityProvisioner.ProvisionAsync(
                services.GetRequiredService<KanbanDbContext>(),
                services.GetRequiredService<IPersonalAccessTokenService>(),
                name,
                rotate,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // InvalidOperationException: personal access tokens are disabled in this configuration.
            await error.WriteLineAsync(exception.Message);
            return 1;
        }

        await output.WriteLineAsync(
            $"Service identity '{identity.User.DisplayName}' ({identity.User.Id}) {(identity.Created ? "created" : "already existed")}.");
        if (rotate)
        {
            await output.WriteLineAsync($"Revoked {identity.RevokedTokens} existing token(s).");
        }

        await output.WriteLineAsync($"full-scope token: {identity.FullToken.PlaintextToken}");
        await output.WriteLineAsync($"read-scope token: {identity.ReadToken.PlaintextToken}");
        await output.WriteLineAsync("Store both now. They are not shown again.");
        return 0;
    }
}
