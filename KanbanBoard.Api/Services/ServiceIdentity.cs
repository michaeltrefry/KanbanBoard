using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace KanbanBoard.Api.Services;

public enum ServiceIdentityCreateOutcome
{
    Created,
    /// <summary>An identity with that name exists; nothing was written.</summary>
    Exists,
    /// <summary>Other creates held the create lock for too long; nothing was written.</summary>
    Busy
}

/// <param name="User">The created identity, or the existing one that has the name (null only if it vanished meanwhile).</param>
public sealed record ServiceIdentityCreateResult(ServiceIdentityCreateOutcome Outcome, AppUser? User = null);

public enum ServiceIdentityTokenOutcome
{
    Issued,
    IdentityNotFound,
    IdentityDisabled,
    TokenNotFound,
    TokenRevoked
}

/// <param name="Identity">The service identity, when there is one.</param>
/// <param name="Token">The token issued, with its plaintext, when <see cref="Outcome"/> is <see cref="ServiceIdentityTokenOutcome.Issued"/>.</param>
public sealed record ServiceIdentityTokenResult(
    ServiceIdentityTokenOutcome Outcome,
    AppUser? Identity = null,
    CreatedPersonalAccessToken? Token = null);

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

    /// <summary>The named lock that every create of a service identity, from the admin page or the command, holds.</summary>
    public const string CreateLockName = "kanban_service_identity_creates";
    public const int CreateLockTimeoutSeconds = 10;

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
    /// Creates a service identity named <paramref name="displayName"/> (already <see cref="NormalizeName"/>d), or, when
    /// one with that name exists as the unique index compares names (any case; on MariaDB any accent as well), writes
    /// nothing and reports that identity. Creates run one at a time, as do those of the create-service-identity command,
    /// so on MariaDB a name is looked up before the insert that would fail on it and no failed insert is logged.
    /// </summary>
    public static Task<ServiceIdentityCreateResult> CreateAsync(KanbanDbContext dbContext, string displayName, CancellationToken cancellationToken)
    {
        var subject = SubjectOf(displayName);
        return MariaDbNamedLock.RunAsync(dbContext, CreateLockName, CreateLockTimeoutSeconds, new ServiceIdentityCreateResult(ServiceIdentityCreateOutcome.Busy), async () =>
        {
            if (await FindAsync(dbContext, subject, cancellationToken) is { } existing)
            {
                return new ServiceIdentityCreateResult(ServiceIdentityCreateOutcome.Exists, existing);
            }

            var now = DateTimeOffset.UtcNow;
            var user = new AppUser { Issuer = Issuer, Subject = subject, DisplayName = displayName, CreatedAtUtc = now, UpdatedAtUtc = now };
            dbContext.AppUsers.Add(user);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return new ServiceIdentityCreateResult(ServiceIdentityCreateOutcome.Created, user);
            }
            catch (DbUpdateException exception)
            {
                // Another create of the name committed between the look-up and the insert, which the lock leaves only
                // to a writer that does not take it (SQLite takes none). On MariaDB the server says so and is taken at
                // its word; elsewhere the name is looked up again.
                dbContext.ChangeTracker.Clear();
                var duplicate = exception.InnerException is MySqlException { Number: DuplicateEntryError } mySqlException &&
                    mySqlException.Message.Contains(IdentityIndexName, StringComparison.Ordinal);
                var winner = await FindAsync(dbContext, subject, cancellationToken);
                if (duplicate || winner is not null)
                {
                    return new ServiceIdentityCreateResult(ServiceIdentityCreateOutcome.Exists, winner);
                }

                throw;
            }
        }, cancellationToken);
    }

    /// <summary>The service identity whose subject equals <paramref name="subject"/> as the database compares it.</summary>
    private static Task<AppUser?> FindAsync(KanbanDbContext dbContext, string subject, CancellationToken cancellationToken) =>
        dbContext.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Issuer == Issuer && candidate.Subject == subject, cancellationToken);

    /// <summary>
    /// Issues a token for the service identity in one transaction that holds the identity's row first: a disable of it
    /// waits for this issue (and then revokes the token) or this issue sees the disable and writes nothing. The token
    /// records <paramref name="admin"/> as the admin who issued it.
    /// </summary>
    public static async Task<ServiceIdentityTokenResult> IssueTokenAsync(
        KanbanDbContext dbContext,
        IPersonalAccessTokenService personalAccessTokenService,
        Guid identityId,
        string name,
        DateTimeOffset? expiresAtUtc,
        string scope,
        ChangeActor? admin,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var (refusal, identity) = await HoldEnabledAsync(dbContext, identityId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var created = await personalAccessTokenService.CreateAsync(identity!, name, expiresAtUtc, scope, cancellationToken, admin);
        await transaction.CommitAsync(cancellationToken);
        return new ServiceIdentityTokenResult(ServiceIdentityTokenOutcome.Issued, identity, created);
    }

    /// <summary>
    /// Replaces the identity's token <paramref name="tokenId"/> with a new one of the same name and scope that expires
    /// at <paramref name="expiresAtUtc"/>, and revokes the old one only once the new one is stored, all in one
    /// transaction that holds the identity's row first. Every write of the identity's tokens (issue, rotate, revoke,
    /// disable, the command's rotate) holds that row, so rotates of one token run one at a time: the first replaces
    /// it, the others find it revoked and write nothing. A revoked token is not rotated; an
    /// expired one is. Whatever is refused or fails, nothing is written and the old token stays as it was.
    /// <paramref name="admin"/> is recorded as the admin who issued the replacement and who revoked the old token.
    /// </summary>
    public static async Task<ServiceIdentityTokenResult> RotateTokenAsync(
        KanbanDbContext dbContext,
        IPersonalAccessTokenService personalAccessTokenService,
        Guid identityId,
        Guid tokenId,
        DateTimeOffset? expiresAtUtc,
        ChangeActor? admin,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var (refusal, identity) = await HoldEnabledAsync(dbContext, identityId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var old = await dbContext.PersonalAccessTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(token => token.Id == tokenId && token.AppUserId == identityId, cancellationToken);
        if (old is null)
        {
            return new ServiceIdentityTokenResult(ServiceIdentityTokenOutcome.TokenNotFound, identity);
        }

        if (old.RevokedAtUtc is not null)
        {
            return new ServiceIdentityTokenResult(ServiceIdentityTokenOutcome.TokenRevoked, identity);
        }

        var created = await personalAccessTokenService.CreateAsync(identity!, old.Name, expiresAtUtc, old.Scope, cancellationToken, admin);
        var (revokedById, revokedByName) = (admin?.Id, admin?.Name);
        await dbContext.PersonalAccessTokens
            .Where(token => token.Id == tokenId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAtUtc, created.Token.CreatedAtUtc)
                .SetProperty(token => token.RevokedByUserId, revokedById)
                .SetProperty(token => token.RevokedByName, revokedByName), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ServiceIdentityTokenResult(ServiceIdentityTokenOutcome.Issued, identity, created);
    }

    /// <summary>
    /// Revokes the service identity's token <paramref name="tokenId"/>; revoking a revoked token keeps the time it was
    /// first revoked and the admin who first revoked it. False when the identity is no service identity or the token is
    /// not one of its tokens. Holds the identity's row first, as every write of its tokens does, so a revoke and a
    /// rotate of one token run one after the other.
    /// </summary>
    public static async Task<bool> RevokeTokenAsync(
        KanbanDbContext dbContext,
        Guid identityId,
        Guid tokenId,
        ChangeActor? admin,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(dbContext, identityId, cancellationToken) == 0)
        {
            return false;
        }

        var token = await dbContext.PersonalAccessTokens
            .FirstOrDefaultAsync(candidate => candidate.Id == tokenId && candidate.AppUserId == identityId, cancellationToken);
        if (token is null)
        {
            return false;
        }

        if (token.RevokedAtUtc is null)
        {
            token.RevokedAtUtc = DateTimeOffset.UtcNow;
            token.RevokedByUserId = admin?.Id;
            token.RevokedByName = admin?.Name;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The first statements of a transaction that writes a token for the service identity: <see cref="LockAsync"/>, then
    /// the read of the identity, which therefore sees a disable committed before the hold. Returns the refusal, or the
    /// identity when it is an enabled service identity.
    /// </summary>
    private static async Task<(ServiceIdentityTokenResult? Refusal, AppUser? Identity)> HoldEnabledAsync(
        KanbanDbContext dbContext,
        Guid identityId,
        CancellationToken cancellationToken)
    {
        var identity = await LockAsync(dbContext, identityId, cancellationToken) == 0
            ? null
            : await dbContext.AppUsers.FirstOrDefaultAsync(user => user.Id == identityId, cancellationToken);
        if (identity is null)
        {
            return (new ServiceIdentityTokenResult(ServiceIdentityTokenOutcome.IdentityNotFound), null);
        }

        return identity.DisabledAtUtc is null
            ? (null, identity)
            : (new ServiceIdentityTokenResult(ServiceIdentityTokenOutcome.IdentityDisabled, identity), identity);
    }

    /// <summary>
    /// Disables the service identity and revokes every token of it that is not revoked yet, in one transaction.
    /// Disabling a disabled identity keeps the time it was first disabled. Each token it revokes records
    /// <paramref name="admin"/> as its revoker; a token revoked before keeps its own. False when there is no such identity.
    /// </summary>
    public static async Task<bool> DisableAsync(KanbanDbContext dbContext, Guid identityId, ChangeActor? admin, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(dbContext, identityId, cancellationToken) == 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var (revokedById, revokedByName) = (admin?.Id, admin?.Name);
        await dbContext.AppUsers
            .Where(user => user.Id == identityId && user.DisabledAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.DisabledAtUtc, now)
                .SetProperty(user => user.UpdatedAtUtc, now), cancellationToken);
        await dbContext.PersonalAccessTokens
            .Where(token => token.AppUserId == identityId && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAtUtc, now)
                .SetProperty(token => token.RevokedByUserId, revokedById)
                .SetProperty(token => token.RevokedByName, revokedByName), cancellationToken);
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

        var relational = dbContext.Database.IsRelational();
        if (!relational)
        {
            return await ProvisionHeldAsync(dbContext, personalAccessTokenService, displayName, rotate, relational, cancellationToken);
        }

        // Under the lock of every create, so that a create of the name from the admin page or another run of the
        // command waits instead of failing on the unique index.
        return await MariaDbNamedLock.RunAsync<ProvisionedServiceIdentity?>(dbContext, CreateLockName, CreateLockTimeoutSeconds, null,
                async () => await ProvisionHeldAsync(dbContext, personalAccessTokenService, displayName, rotate, relational, cancellationToken),
                cancellationToken) ??
            throw new InvalidOperationException(
                $"Other service identity creates were in progress for {CreateLockTimeoutSeconds} s. Nothing was written; try again.");
    }

    private static async Task<ProvisionedServiceIdentity> ProvisionHeldAsync(
        KanbanDbContext dbContext,
        IPersonalAccessTokenService personalAccessTokenService,
        string displayName,
        bool rotate,
        bool relational,
        CancellationToken cancellationToken)
    {
        // One transaction on a real database: a failure part-way leaves no identity, no unprinted token, no revocation.
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
