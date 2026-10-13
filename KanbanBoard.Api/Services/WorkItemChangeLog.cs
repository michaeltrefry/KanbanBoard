using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;

namespace KanbanBoard.Api.Services;

/// <summary>
/// Who made a change. It is taken from the request, never from a field of it: the user behind the forwarded
/// personal access token, or the user of the browser session.
/// </summary>
public sealed record ChangeActor(Guid? Id, string? Name)
{
    public static ChangeActor? From(AppUser? user) =>
        user is null ? null : new(user.Id, WorkItemClaims.ClaimantNameFor(user));

    /// <summary>
    /// The actor of the request, or null when it carries neither a token nor a session (authentication is off).
    /// Call it before the write's transaction starts: a read inside it would come before the statement that locks
    /// the item's row.
    /// </summary>
    public static async Task<ChangeActor?> ResolveAsync(HttpContext httpContext, KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        if (KanbanInternalApiExtensions.IsInternalApiRequest(httpContext))
        {
            // Whether the token may write was decided before the endpoint ran; this only names its owner.
            return Guid.TryParse(httpContext.User.FindFirst(KanbanInternalAuth.PersonalAccessTokenIdHeaderName)?.Value, out var tokenId)
                ? From(await dbContext.PersonalAccessTokens
                    .AsNoTracking()
                    .Where(token => token.Id == tokenId)
                    .Select(token => token.AppUser)
                    .FirstOrDefaultAsync(cancellationToken))
                : null;
        }

        var currentUser = httpContext.RequestServices.GetService<ICurrentUserService>();
        if (currentUser is null || !currentUser.TryGetExternalIdentity(httpContext.User, out var identity))
        {
            return null;
        }

        var user = await dbContext.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Issuer == identity.Issuer && candidate.Subject == identity.Subject, cancellationToken);
        if (user is not null)
        {
            return From(user);
        }

        // A session whose user row was never created (it is created by GET /api/auth/session).
        var name = identity.DisplayName ?? identity.Email ?? identity.Subject;
        return new(null, name.Length <= WorkItemClaimLimits.MaxClaimantNameLength ? name : name[..WorkItemClaimLimits.MaxClaimantNameLength]);
    }
}

/// <summary>A write that waited too long for other callers and changed nothing. The API answers 503 <c>busy</c>.</summary>
public sealed class KanbanBusyException(string message) : Exception(message);

/// <summary>
/// The persisted change feed: one entry per change to a work item, written in the transaction of the write it
/// describes. Every write to a work item ends with <see cref="CommitAsync"/>; nothing else adds entries. Everything
/// here runs on SQLite in the tests and on MariaDB through MySql.EntityFrameworkCore in production.
/// </summary>
public static class WorkItemChangeLog
{
    public const string LockName = "kanban_work_item_changes";
    public const int LockTimeoutSeconds = 10;

    /// <summary>
    /// The most characters of one text an entry stores. A longer <c>old</c> or <c>new</c> value is cut to this many
    /// and marked (<c>old_truncated</c> / <c>new_truncated</c>); the item itself holds the whole text.
    /// </summary>
    public const int MaxStoredTextLength = 16_384;

    /// <summary>How many times a write that MariaDB picked as the victim of a deadlock is run before it is refused <c>busy</c>.</summary>
    public const int DeadlockAttempts = 3;

    private const int MySqlDeadlock = 1213;

    /// <summary>The kind of a cursor of <c>GET /api/items/changes</c> (see <see cref="WorkItemCursor"/> for the others).</summary>
    public const string CursorKind = "h";

    // The JSON is stored, not put into a page: text is kept as it is rather than escaped character by character,
    // which would make a comment in a non-Latin script six times its size.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The fields of an item that an update, a move, a create or a delete can change, as the feed names them.</summary>
    public static Dictionary<string, object?> Snapshot(WorkItem item) => new()
    {
        ["title"] = item.Title,
        ["description"] = item.Description,
        ["type"] = item.Type,
        ["status"] = item.Status,
        ["priority"] = item.Priority,
        ["order"] = item.Order,
        ["estimate"] = item.Estimate,
        ["labels"] = item.Labels,
        ["epic_id"] = item.EpicId,
        ["version"] = item.Version
    };

    /// <summary>The stored claim of an item, as the feed names it. A claim that has expired is still what is stored.</summary>
    public static Dictionary<string, object?> ClaimSnapshot(Guid? claimantUserId, string? claimantName, string? marker, DateTimeOffset? expiresAtUtc) => new()
    {
        ["claimant_id"] = claimantUserId,
        ["claimant"] = claimantName,
        ["claim_marker"] = marker,
        ["claim_expires_at_utc"] = expiresAtUtc
    };

    /// <summary>
    /// An entry that lists every field whose value differs between <paramref name="before"/> and
    /// <paramref name="after"/>. A missing side is all nulls: null before for something new, null after for
    /// something removed.
    /// </summary>
    public static WorkItemChange Entry(
        Guid itemId,
        Guid projectId,
        string kind,
        ChangeActor? actor,
        DateTimeOffset now,
        IReadOnlyDictionary<string, object?>? before,
        IReadOnlyDictionary<string, object?>? after)
    {
        var changes = new JsonArray();
        foreach (var field in (before?.Keys ?? []).Union(after?.Keys ?? []))
        {
            var oldValue = before?.GetValueOrDefault(field);
            var newValue = after?.GetValueOrDefault(field);
            if (!Equals(oldValue, newValue))
            {
                var change = new JsonObject
                {
                    ["field"] = field,
                    ["old"] = Stored(oldValue, out var oldTruncated),
                    ["new"] = Stored(newValue, out var newTruncated)
                };
                // Present only when true: the two values are compared whole, so both can be cut to the same text.
                if (oldTruncated)
                {
                    change["old_truncated"] = true;
                }

                if (newTruncated)
                {
                    change["new_truncated"] = true;
                }

                changes.Add(change);
            }
        }

        return new WorkItemChange
        {
            WorkItemId = itemId,
            ProjectId = projectId,
            Kind = kind,
            ActorUserId = actor?.Id,
            ActorName = actor?.Name,
            Changes = changes.ToJsonString(JsonOptions),
            // The precision MariaDB stores.
            CreatedAtUtc = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond))
        };
    }

    // A value as an entry stores it: every text in it (the value itself, or a field of a comment or a link) is cut
    // to MaxStoredTextLength characters. An update of a 9 MB description would otherwise store 18 MB in one row,
    // which MariaDB refuses (max_allowed_packet), and a page of the feed would be as large as its largest items.
    private static JsonNode? Stored(object? value, out bool truncated)
    {
        truncated = false;
        if (value is string text)
        {
            return JsonValue.Create(Cut(text, ref truncated));
        }

        var node = JsonSerializer.SerializeToNode(value, JsonOptions);
        if (node is JsonObject fields)
        {
            foreach (var (name, inner) in fields.ToList())
            {
                if (inner is JsonValue innerValue && innerValue.TryGetValue<string>(out var innerText) && innerText.Length > MaxStoredTextLength)
                {
                    fields[name] = Cut(innerText, ref truncated);
                }
            }
        }

        return node;
    }

    private static string Cut(string text, ref bool truncated)
    {
        if (text.Length <= MaxStoredTextLength)
        {
            return text;
        }

        truncated = true;
        // Not between the two halves of one character.
        return text[..(char.IsHighSurrogate(text[MaxStoredTextLength - 1]) ? MaxStoredTextLength - 1 : MaxStoredTextLength)];
    }

    /// <summary>
    /// Runs <paramref name="write"/>, which starts and commits its own transaction, and runs it again when MariaDB
    /// rolled it back as the victim of a deadlock (error 1213): a write whose first statement takes the rows of
    /// several items can meet writers that hold one of them and wait for another. After
    /// <see cref="DeadlockAttempts"/> deadlocks the write is refused <c>busy</c>; nothing of it is stored.
    ///
    /// With <paramref name="alsoWhenARowVanished"/> the write is run again in the same way, and counted with the
    /// deadlocks, when a row it had read was deleted before it saved it (<see cref="DbUpdateConcurrencyException"/>):
    /// the move renumbers the siblings it read. The write must leave its transaction uncommitted when that save
    /// throws, so that nothing of the attempt is stored.
    /// </summary>
    public static async Task<T> RetryDeadlocksAsync<T>(KanbanDbContext dbContext, Func<Task<T>> write, bool alsoWhenARowVanished = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await write();
            }
            catch (Exception exception) when (IsDeadlock(exception) || (alsoWhenARowVanished && exception is DbUpdateConcurrencyException))
            {
                // The transaction has been rolled back, by the server or when the write left it; what the attempt
                // tracked is not stored.
                dbContext.ChangeTracker.Clear();
                if (attempt == DeadlockAttempts)
                {
                    throw new KanbanBusyException(exception is DbUpdateConcurrencyException
                        ? $"Other writes deleted work items this one was renumbering, {DeadlockAttempts} times. Nothing was changed; try again."
                        : $"Other writes to the same work items deadlocked with this one {DeadlockAttempts} times. Nothing was changed; try again.");
                }
            }
        }
    }

    private static bool IsDeadlock(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is MySqlException { Number: MySqlDeadlock })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the item's row until the transaction ends and changes nothing. It must be the first statement of the
    /// transaction of a write whose own UPDATE is conditional or overwrites what the entry has to report: the read
    /// that follows then returns the values that write replaces, and not ones another writer is about to change.
    /// </summary>
    public static Task LockItemAsync(KanbanDbContext dbContext, Guid itemId, CancellationToken cancellationToken) =>
        dbContext.WorkItems
            .Where(item => item.Id == itemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), cancellationToken);

    /// <summary>The project of an item whose row the transaction holds. A project id never changes.</summary>
    public static Task<Guid> ProjectIdAsync(KanbanDbContext dbContext, Guid itemId, CancellationToken cancellationToken) =>
        dbContext.WorkItems
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => item.ProjectId)
            .FirstAsync(cancellationToken);

    /// <summary>
    /// Appends <paramref name="entries"/> and commits <paramref name="transaction"/>, so the entries are stored
    /// exactly when the write they describe is. Call it last, after every other statement of the write.
    ///
    /// The insert and the commit run under one lock, one writer at a time (on MariaDB the named lock
    /// <see cref="LockName"/>; SQLite admits one writer anyway). The database numbers an entry when it is inserted,
    /// so an entry with a higher sequence is always committed later: a reader that has seen sequence N will never
    /// find a new entry at or below N. Without the lock a writer could take N, a second one take N+1 and commit
    /// first, and a reader would pass N before it existed. The lock is taken after the write's row locks and its
    /// holder waits for nothing but its own INSERT and COMMIT, so it cannot be part of a deadlock.
    /// </summary>
    /// <exception cref="KanbanBusyException">The lock was not free within <see cref="LockTimeoutSeconds"/>; nothing is committed.</exception>
    public static async Task CommitAsync(
        KanbanDbContext dbContext,
        IDbContextTransaction transaction,
        IReadOnlyCollection<WorkItemChange> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var committed = await MariaDbNamedLock.RunAsync(dbContext, LockName, LockTimeoutSeconds, false, async () =>
        {
            // Nothing follows this save in the transaction, so there is nothing a savepoint could go back to:
            // without one the lock is held for two statements less.
            dbContext.Database.AutoSavepointsEnabled = false;
            dbContext.WorkItemChanges.AddRange(entries);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }, cancellationToken);
        if (!committed)
        {
            throw new KanbanBusyException(
                $"Other work item changes were being recorded for {LockTimeoutSeconds} s. Nothing was changed; try again.");
        }
    }

    public static Task CommitAsync(
        KanbanDbContext dbContext,
        IDbContextTransaction transaction,
        WorkItemChange entry,
        CancellationToken cancellationToken) =>
        CommitAsync(dbContext, transaction, [entry], cancellationToken);

    public static string EncodeCursor(long sequence) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CursorKind}.{sequence.ToString(CultureInfo.InvariantCulture)}"));

    public static bool TryDecodeCursor(string value, out long sequence)
    {
        sequence = 0;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value.Trim())).Split('.');
            return parts.Length == 2 &&
                parts[0] == CursorKind &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static WorkItemChangeDto ToDto(WorkItemChange change)
    {
        using var changes = JsonDocument.Parse(change.Changes);
        return new(
            change.Sequence,
            change.WorkItemId,
            change.ProjectId,
            change.Kind,
            change.ActorUserId is null && change.ActorName is null ? null : new WorkItemChangeActorDto(change.ActorUserId, change.ActorName),
            changes.RootElement.Clone(),
            change.CreatedAtUtc);
    }

    /// <summary>
    /// Answers a write that <see cref="CommitAsync"/> gave up on with 503 <c>busy</c>, like the other writes that
    /// wait for a lock.
    /// </summary>
    public static IApplicationBuilder UseKanbanBusyRefusal(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (KanbanBusyException exception) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new KanbanErrorDto(KanbanErrorCodes.Busy, exception.Message));
            }
        });
}
