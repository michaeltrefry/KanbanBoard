using System.Security.Cryptography;
using System.Text;
using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Api.Services;

/// <summary>
/// The idempotency key of a create, the same for every create that takes one. A repeat of a key returns the object
/// the key already belongs to and creates nothing. Only <see cref="Hash"/> is stored, under a unique index.
/// </summary>
public static class IdempotencyKeys
{
    /// <summary>The refusal of a key that is not <see cref="IsValid"/>, naming the argument as the MCP tools spell it.</summary>
    public static readonly string InvalidMessage =
        $"Argument 'idempotency_key' must be 1 to {IdempotencyKeyLimits.MaxLength} printable ASCII characters without spaces.";

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
}
