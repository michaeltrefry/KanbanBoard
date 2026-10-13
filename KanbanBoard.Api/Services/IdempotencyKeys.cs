using System.Security.Cryptography;
using System.Text;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;

namespace KanbanBoard.Api.Services;

public enum KeyedCreateOutcome
{
    Created,
    /// <summary>The idempotency key already belongs to an object: nothing was written.</summary>
    Existing,
    /// <summary>The parent the object is created in does not exist.</summary>
    NotFound,
    /// <summary>The object is not acceptable where it is created: nothing was written.</summary>
    Invalid
}

/// <param name="Id">The object that was created, or the one the idempotency key already belonged to.</param>
/// <param name="Message">Why the create was refused.</param>
public sealed record KeyedCreateResult(KeyedCreateOutcome Outcome, Guid? Id = null, string? Message = null);

/// <summary>
/// The idempotency key of a create, the same for every create that takes one. A repeat of a key returns the object
/// the key already belongs to and creates nothing. Only <see cref="Hash"/> is stored, under a unique index.
/// </summary>
public static class IdempotencyKeys
{
    /// <summary>The refusal of a key that is not <see cref="IsValid"/>, naming the argument as the MCP tools spell it.</summary>
    public static readonly string InvalidMessage =
        $"Argument 'idempotency_key' must be 1 to {IdempotencyKeyLimits.MaxLength} printable ASCII characters without spaces.";

    public const string InactiveTokenMessage =
        "The forwarded personal access token is revoked or expired and cannot be used with an idempotency key.";

    // ER_DUP_ENTRY.
    private const int DuplicateEntryError = 1062;

    /// <summary>
    /// A key is 1 to <see cref="IdempotencyKeyLimits.MaxLength"/> printable ASCII characters without a space. Keys
    /// are compared exactly.
    /// </summary>
    public static bool IsValid(string key) =>
        key.Length is > 0 and <= IdempotencyKeyLimits.MaxLength &&
        key.All(character => character is > ' ' and <= '~');

    /// <summary>
    /// SHA-256, in lowercase hex, of the key together with the identity that used it: a key belongs to one identity,
    /// and every caller without a token identity shares one. The hash is what is stored and compared, so keys match
    /// exactly where the database's default collation would ignore case.
    /// </summary>
    public static string Hash(Guid? identityUserId, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{identityUserId:N}:{key}")));

    /// <summary>
    /// The hash of the idempotency key of a request, for the identity of its token, or the refusal of the request:
    /// <c>invalid_argument</c> for a malformed key and <c>scope_denied</c> for a token that no longer resolves to an
    /// identity, which must not fall back to the identity that callers without a token share. Both null without a key.
    /// Call it before the write's transaction starts.
    /// </summary>
    public static async Task<(string? Hash, IResult? Refusal)> HashForRequestAsync(
        HttpContext httpContext,
        KanbanDbContext dbContext,
        string? key,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (key is null)
        {
            return (null, null);
        }

        if (!IsValid(key))
        {
            return (null, Results.Json(new KanbanErrorDto(KanbanErrorCodes.InvalidArgument, InvalidMessage), statusCode: StatusCodes.Status400BadRequest));
        }

        AppUser? identity = null;
        if (KanbanInternalApiExtensions.IsInternalApiRequest(httpContext) &&
            httpContext.User.FindFirst(KanbanInternalAuth.PersonalAccessTokenIdHeaderName) is not null)
        {
            identity = await KanbanClaimEndpointRouteBuilderExtensions.ResolveClaimantAsync(httpContext, dbContext, now, cancellationToken);
            if (identity is null)
            {
                return (null, Results.Json(new KanbanErrorDto(KanbanErrorCodes.ScopeDenied, InactiveTokenMessage), statusCode: StatusCodes.Status403Forbidden));
            }
        }

        return (Hash(identity?.Id, key), null);
    }

    /// <summary>
    /// Runs a keyed create under the row of its parent, so that the creates with one key in one parent run one at a
    /// time and the second finds what the first created instead of failing on the unique index.
    ///
    /// <paramref name="lockParent"/> must be an UPDATE of the parent's row that changes nothing and returns the rows
    /// it matched. It is the first statement of the transaction on purpose: it holds the row until the transaction
    /// ends, and on MariaDB (REPEATABLE READ) the reads that follow it see everything the earlier holders committed.
    /// A read before it would fix the transaction's view too early. An INSERT of a child takes a shared lock on the
    /// same row, so creates without a key wait for a keyed one and never deadlock with it: they hold nothing yet.
    ///
    /// Should the unique index <paramref name="uniqueIndexName"/> refuse the INSERT all the same, the server is taken
    /// at its word (error 1062) and the key is looked up again after the transaction is gone; inside it the row that
    /// won could not be seen. A deadlock (error 1213) is run again, see <see cref="WorkItemChangeLog.RetryDeadlocksAsync"/>.
    /// </summary>
    /// <param name="findExisting">The id of the object the key already belongs to in this parent, or null.</param>
    /// <param name="create">Inserts the object and commits the transaction it is given.</param>
    public static async Task<KeyedCreateResult> CreateOnceAsync(
        KanbanDbContext dbContext,
        Func<Task<int>> lockParent,
        Func<Task<Guid?>> findExisting,
        Func<IDbContextTransaction, Task<KeyedCreateResult>> create,
        string uniqueIndexName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                if (await lockParent() == 0)
                {
                    return new KeyedCreateResult(KeyedCreateOutcome.NotFound);
                }

                return await findExisting() is { } existing
                    ? new KeyedCreateResult(KeyedCreateOutcome.Existing, existing)
                    : await create(transaction);
            });
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is MySqlException { Number: DuplicateEntryError } duplicate &&
            duplicate.Message.Contains(uniqueIndexName, StringComparison.Ordinal))
        {
            dbContext.ChangeTracker.Clear();
            if (await findExisting() is { } existing)
            {
                return new KeyedCreateResult(KeyedCreateOutcome.Existing, existing);
            }

            throw;
        }
    }
}
