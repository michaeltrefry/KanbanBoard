using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

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

    public static readonly string InvalidNameMessage =
        $"A service identity name is required and must be at most {MaxNameLength} characters.";

    // The unique index on (Issuer, Subject), as the server names it in error 1062 (ER_DUP_ENTRY).
    private const string IdentityIndexName = "IX_AppUsers_Issuer_Subject";
    private const int DuplicateEntryError = 1062;

    /// <summary>
    /// The name rules of every way a service identity is created: the name is trimmed and must then be 1 to
    /// <see cref="MaxNameLength"/> characters. Returns the trimmed name, or null when the name breaks them.
    /// </summary>
    public static string? NormalizeName(string? name)
    {
        var displayName = name?.Trim() ?? string.Empty;
        return displayName.Length is 0 or > MaxNameLength ? null : displayName;
    }

    /// <summary>The subject that identifies a service identity: its name in lowercase, so names differing in case are one identity.</summary>
    public static string SubjectOf(string displayName) => displayName.ToLowerInvariant();

    public static string DisabledMessage(string name) =>
        $"Service identity '{name}' is disabled: it gets no new token until an admin enables it.";

    /// <summary>
    /// Holds the identity's row, without changing it, until the transaction ends, and returns the rows it matched (0
    /// for no such service identity). It must be the first statement of the transaction: on MariaDB (REPEATABLE READ)
    /// the reads that follow it then see what an earlier disable or enable committed, so an issue and a disable of one
    /// identity run one after the other and no token is issued for a disabled identity.
    /// </summary>
    public static Task<int> LockAsync(KanbanDbContext dbContext, Guid identityId, CancellationToken cancellationToken) =>
        dbContext.AppUsers
            .Where(user => user.Id == identityId && user.Issuer == Issuer)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.DisabledAtUtc, user => user.DisabledAtUtc), cancellationToken);

    /// <summary>
    /// Creates a service identity named <paramref name="displayName"/> (already <see cref="NormalizeName"/>d), or
    /// returns null when one with that name, in any case, exists; nothing is written then.
    /// </summary>
    public static async Task<AppUser?> CreateAsync(KanbanDbContext dbContext, string displayName, CancellationToken cancellationToken)
    {
        var subject = SubjectOf(displayName);
        if (await dbContext.AppUsers.AnyAsync(user => user.Issuer == Issuer && user.Subject == subject, cancellationToken))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser { Issuer = Issuer, Subject = subject, DisplayName = displayName, CreatedAtUtc = now, UpdatedAtUtc = now };
        dbContext.AppUsers.Add(user);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException exception)
        {
            // Another create of the name committed between the check and the insert. On MariaDB the server says so
            // and is taken at its word; elsewhere (SQLite) the name is looked up again, outside any transaction.
            dbContext.ChangeTracker.Clear();
            if (exception.InnerException is MySqlException { Number: DuplicateEntryError } duplicate &&
                duplicate.Message.Contains(IdentityIndexName, StringComparison.Ordinal))
            {
                return null;
            }

            if (await dbContext.AppUsers.AsNoTracking().AnyAsync(candidate => candidate.Issuer == Issuer && candidate.Subject == subject, cancellationToken))
            {
                return null;
            }

            throw;
        }
    }

    /// <summary>
    /// Disables the service identity and revokes every token of it that is not revoked yet, in one transaction.
    /// Disabling a disabled identity keeps the time it was first disabled. False when there is no such identity.
    /// </summary>
    public static async Task<bool> DisableAsync(KanbanDbContext dbContext, Guid identityId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(dbContext, identityId, cancellationToken) == 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        await dbContext.AppUsers
            .Where(user => user.Id == identityId && user.DisabledAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.DisabledAtUtc, now)
                .SetProperty(user => user.UpdatedAtUtc, now), cancellationToken);
        await dbContext.PersonalAccessTokens
            .Where(token => token.AppUserId == identityId && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAtUtc, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Enables the service identity so that tokens can be issued for it again. The tokens revoked when it was disabled
    /// stay revoked. False when there is no such identity.
    /// </summary>
    public static async Task<bool> EnableAsync(KanbanDbContext dbContext, Guid identityId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var matched = await dbContext.AppUsers
            .Where(user => user.Id == identityId && user.Issuer == Issuer)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.DisabledAtUtc, (DateTimeOffset?)null)
                .SetProperty(user => user.UpdatedAtUtc, now), cancellationToken);
        return matched > 0;
    }

    public static async Task<ProvisionedServiceIdentity> ProvisionAsync(
        KanbanDbContext dbContext,
        IPersonalAccessTokenService personalAccessTokenService,
        string name,
        bool rotate,
        CancellationToken cancellationToken)
    {
        var displayName = NormalizeName(name) ?? throw new ArgumentException(InvalidNameMessage, nameof(name));

        // Before any write: with tokens disabled nothing is created and nothing is revoked.
        personalAccessTokenService.EnsureEnabled();

        // One transaction on a real database: a failure part-way leaves no identity, no unprinted token, no revocation.
        var relational = dbContext.Database.IsRelational();
        await using var transaction = relational
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var subject = SubjectOf(displayName);
        var now = DateTimeOffset.UtcNow;
        if (relational)
        {
            // The identity's row is held first, as an issue from the admin page holds it, so that a disable of it
            // committed meanwhile is seen below and no token is issued for a disabled identity.
            await dbContext.AppUsers
                .Where(candidate => candidate.Issuer == Issuer && candidate.Subject == subject)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.DisabledAtUtc, candidate => candidate.DisabledAtUtc), cancellationToken);
        }

        var user = await dbContext.AppUsers
            .FirstOrDefaultAsync(candidate => candidate.Issuer == Issuer && candidate.Subject == subject, cancellationToken);
        if (user?.DisabledAtUtc is not null)
        {
            // Nothing is written: a disabled identity is enabled again only from the admin page.
            throw new InvalidOperationException(DisabledMessage(user.DisplayName ?? displayName));
        }

        var created = user is null;
        if (user is null)
        {
            user = new AppUser { Issuer = Issuer, Subject = subject, CreatedAtUtc = now };
            dbContext.AppUsers.Add(user);
        }

        user.DisplayName = displayName;
        user.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        // Loaded before the new pair exists, so the pair is never among the tokens to revoke.
        var previousTokens = rotate && !created
            ? await dbContext.PersonalAccessTokens
                .Where(token => token.AppUserId == user.Id && token.RevokedAtUtc == null)
                .ToListAsync(cancellationToken)
            : [];

        var fullToken = await personalAccessTokenService.CreateAsync(
            user, $"{displayName} ({PersonalAccessTokenScopes.Full})", null, PersonalAccessTokenScopes.Full, cancellationToken);
        var readToken = await personalAccessTokenService.CreateAsync(
            user, $"{displayName} ({PersonalAccessTokenScopes.Read})", null, PersonalAccessTokenScopes.Read, cancellationToken);

        // The old tokens are revoked only once both replacements exist, so a failed issue leaves them usable.
        if (previousTokens.Count > 0)
        {
            foreach (var token in previousTokens)
            {
                token.RevokedAtUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ProvisionedServiceIdentity(user, created, previousTokens.Count, fullToken, readToken);
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
        "               A disabled identity is refused; enable it on the admin page first.\n" +
        "  --rotate     Revoke the identity's existing tokens once the new pair is issued.";

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
            // InvalidOperationException: personal access tokens are disabled in this configuration, or the identity is
            // disabled.
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
