using System.Buffers.Text;
using System.Globalization;
using System.Text;
using KanbanBoard.Api.Models;

namespace KanbanBoard.Api.Services;

public enum WorkItemClaimState
{
    // No live claim: never claimed, released, or the claim has expired.
    Unclaimed,
    // A claim that has not expired.
    Claimed,
    // A claim that expired and was neither released nor replaced. These items are also Unclaimed.
    Expired
}

/// <summary>
/// The optional filters and the cursor of <c>GET /api/items</c>. Everything here is translated to SQL; the same
/// expressions run on SQLite in the tests and on MariaDB through MySql.EntityFrameworkCore in production.
/// </summary>
public static class WorkItemListing
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>
    /// Keeps items that carry <paramref name="label"/> as a whole label, ignoring case. Labels are stored as one
    /// comma-separated string without spaces around the commas (see NormalizeLabels in Program.cs).
    /// </summary>
    public static IQueryable<WorkItem> WhereHasLabel(this IQueryable<WorkItem> items, string label)
    {
        var delimited = $",{label.ToLowerInvariant()},";
        return items.Where(item => item.Labels != null && ("," + item.Labels.ToLower() + ",").Contains(delimited));
    }

    /// <summary>
    /// Claim state as of <paramref name="now"/>. It is compared with the clock on every call and never with a
    /// stored stamp, because a claim expires without any write to the item.
    /// </summary>
    public static IQueryable<WorkItem> WhereClaimState(this IQueryable<WorkItem> items, WorkItemClaimState state, DateTimeOffset now) =>
        state switch
        {
            WorkItemClaimState.Claimed => items.Where(item => item.ClaimExpiresAtUtc > now),
            WorkItemClaimState.Expired => items.Where(item => item.ClaimExpiresAtUtc <= now),
            _ => items.Where(item => item.ClaimExpiresAtUtc == null || item.ClaimExpiresAtUtc <= now)
        };

    /// <summary>The paging order: oldest first. Neither column ever changes, so a moved or edited item keeps its place.</summary>
    public static IQueryable<WorkItem> InPagingOrder(this IQueryable<WorkItem> items) =>
        items.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id);

    /// <summary>Keeps the items that come after <paramref name="cursor"/> in the paging order.</summary>
    public static IQueryable<WorkItem> WhereAfter(this IQueryable<WorkItem> items, WorkItemCursor cursor)
    {
        var createdAtUtc = cursor.CreatedAtUtc;
        var id = cursor.Id;
        return items.Where(item =>
            item.CreatedAtUtc > createdAtUtc ||
            (item.CreatedAtUtc == createdAtUtc && item.Id.CompareTo(id) > 0));
    }

    public static bool TryParseClaimState(string value, out WorkItemClaimState state)
    {
        (var known, state) = value.Trim().ToLowerInvariant() switch
        {
            "unclaimed" => (true, WorkItemClaimState.Unclaimed),
            "claimed" => (true, WorkItemClaimState.Claimed),
            "expired" => (true, WorkItemClaimState.Expired),
            _ => (false, default)
        };
        return known;
    }

    /// <summary>
    /// Parses an ISO 8601 timestamp; one without an offset is read as UTC. The result is cut to whole microseconds,
    /// the precision MariaDB stores, so a stamp copied from a write response still matches the stored value.
    /// </summary>
    public static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp))
        {
            return false;
        }

        timestamp = timestamp.ToUniversalTime();
        timestamp = timestamp.AddTicks(-(timestamp.Ticks % TimeSpan.TicksPerMicrosecond));
        return true;
    }
}

/// <summary>
/// The position of the last item of a page. On the wire it is an opaque string: callers pass back what they were
/// given and never build or read one.
/// </summary>
public readonly record struct WorkItemCursor(DateTimeOffset CreatedAtUtc, Guid Id)
{
    public static WorkItemCursor After(WorkItem item) => new(item.CreatedAtUtc, item.Id);

    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CreatedAtUtc.UtcTicks}.{Id:N}"));

    public static bool TryDecode(string value, out WorkItemCursor cursor)
    {
        cursor = default;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value.Trim())).Split('.');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !Guid.TryParseExact(parts[1], "N", out var id))
            {
                return false;
            }

            cursor = new WorkItemCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
