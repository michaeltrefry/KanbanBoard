using System.Security.Cryptography;
using System.Text;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public enum WorkItemLinkOutcome
{
    Succeeded,
    NotFound
}

/// <summary>
/// External links on work items. A link is identified by its item, kind and URL; URLs are compared exactly,
/// character for character. Everything here runs on SQLite in the tests and on MariaDB through
/// MySql.EntityFrameworkCore in production.
/// </summary>
public static class WorkItemLinks
{
    /// <summary>Accepts one of <see cref="WorkItemLinkKinds"/>, ignoring case and surrounding whitespace.</summary>
    public static bool TryNormalizeKind(string? value, out string kind)
    {
        kind = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return WorkItemLinkKinds.All.Contains(kind);
    }

    /// <summary>Accepts an absolute http or https URL of at most <see cref="WorkItemLinkLimits.MaxUrlLength"/> characters.</summary>
    public static bool TryNormalizeUrl(string? value, out string url)
    {
        url = value?.Trim() ?? string.Empty;
        return url.Length is > 0 and <= WorkItemLinkLimits.MaxUrlLength &&
            Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            parsed.Host.Length > 0;
    }

    public static string HashUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    /// <summary>
    /// Attaches the URL to the item under <paramref name="kind"/>. A link with the same kind and URL that is already
    /// on the item is left as it is. A link that is added stamps the item's UpdatedAtUtc with <paramref name="now"/>.
    /// </summary>
    public static async Task<WorkItemLinkOutcome> AddAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        string kind,
        string url,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAndStampItemAsync(dbContext, itemId, now, cancellationToken))
        {
            return WorkItemLinkOutcome.NotFound;
        }

        var urlHash = HashUrl(url);
        var exists = await dbContext.WorkItemLinks.AnyAsync(
            link => link.WorkItemId == itemId && link.Kind == kind && link.UrlHash == urlHash,
            cancellationToken);
        if (exists)
        {
            // Nothing changed: leaving the transaction uncommitted takes the stamp back.
            return WorkItemLinkOutcome.Succeeded;
        }

        dbContext.WorkItemLinks.Add(new WorkItemLink
        {
            WorkItemId = itemId,
            Kind = kind,
            Url = url,
            UrlHash = urlHash,
            CreatedAtUtc = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return WorkItemLinkOutcome.Succeeded;
    }

    /// <summary>
    /// Removes one link of the item: the one with id <paramref name="linkId"/>, or, when that is null, the one with
    /// <paramref name="kind"/> and <paramref name="url"/>. Removing a link the item does not carry succeeds and
    /// changes nothing. A link that is removed stamps the item's UpdatedAtUtc with <paramref name="now"/>.
    /// </summary>
    public static async Task<WorkItemLinkOutcome> RemoveAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        Guid? linkId,
        string? kind,
        string? url,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAndStampItemAsync(dbContext, itemId, now, cancellationToken))
        {
            return WorkItemLinkOutcome.NotFound;
        }

        var urlHash = url is null ? null : HashUrl(url);
        var link = linkId is not null
            ? await dbContext.WorkItemLinks.FirstOrDefaultAsync(
                candidate => candidate.Id == linkId && candidate.WorkItemId == itemId, cancellationToken)
            : await dbContext.WorkItemLinks.FirstOrDefaultAsync(
                candidate => candidate.WorkItemId == itemId && candidate.Kind == kind && candidate.UrlHash == urlHash, cancellationToken);
        if (link is null)
        {
            return WorkItemLinkOutcome.Succeeded;
        }

        // A tracked delete, not ExecuteDelete: MySql.EntityFrameworkCore writes that as DELETE FROM ... AS `w`,
        // which MariaDB rejects.
        dbContext.WorkItemLinks.Remove(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return WorkItemLinkOutcome.Succeeded;
    }

    // Must be the first statement of the transaction. The UPDATE holds the item's row until the transaction ends, so
    // the link writes of one item run one at a time: the second of two identical adds waits here and then finds the
    // first one's link, instead of failing on the unique index. Do not replace it with a plain read of the item.
    private static async Task<bool> LockAndStampItemAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.WorkItems
            .Where(item => item.Id == itemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, now), cancellationToken) > 0;

    /// <summary>Keeps the items that carry <paramref name="url"/> under any kind.</summary>
    public static IQueryable<WorkItem> WhereHasLink(this IQueryable<WorkItem> items, KanbanDbContext dbContext, string url)
    {
        var urlHash = HashUrl(url);
        return items.Where(item => dbContext.WorkItemLinks.Any(link => link.WorkItemId == item.Id && link.UrlHash == urlHash));
    }

    /// <summary>Loads the links of every item in <paramref name="items"/>, keyed by work item id, oldest first.</summary>
    public static async Task<ILookup<Guid, WorkItemLinkDto>> LoadAsync(
        KanbanDbContext dbContext,
        IQueryable<WorkItem> items,
        CancellationToken cancellationToken)
    {
        var links = await dbContext.WorkItemLinks
            .AsNoTracking()
            .Where(link => items.Any(item => item.Id == link.WorkItemId))
            .ToListAsync(cancellationToken);

        return links
            .OrderBy(link => link.CreatedAtUtc)
            .ThenBy(link => link.Id)
            .ToLookup(link => link.WorkItemId, link => new WorkItemLinkDto(link.Id, link.Kind, link.Url, link.CreatedAtUtc));
    }

    public static Task<ILookup<Guid, WorkItemLinkDto>> LoadAsync(
        KanbanDbContext dbContext,
        Guid itemId,
        CancellationToken cancellationToken) =>
        LoadAsync(dbContext, dbContext.WorkItems.Where(item => item.Id == itemId), cancellationToken);
}
