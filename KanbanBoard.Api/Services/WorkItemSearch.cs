using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

/// <summary>
/// Case-insensitive word search over work item titles and descriptions.
/// Every whitespace-separated term must appear in the title or the description.
/// Both sides are lowered explicitly so matching does not depend on the column collation.
/// </summary>
public static class WorkItemSearch
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public static bool TryNormalizeTerms(string? query, out IReadOnlyList<string> terms)
    {
        terms = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.ToLowerInvariant())
            .Distinct()
            .ToList();

        return terms.Count > 0;
    }

    public static async Task<IReadOnlyList<WorkItemSearchResultDto>> SearchAsync(
        KanbanDbContext dbContext,
        string? query,
        Guid? projectId,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeTerms(query, out var terms))
        {
            return [];
        }

        var pageSize = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = dbContext.WorkItems.AsNoTracking();

        if (projectId is not null)
        {
            items = items.Where(item => item.ProjectId == projectId);
        }

        foreach (var term in terms)
        {
            items = items.Where(item =>
                item.Title.ToLower().Contains(term) ||
                (item.Description != null && item.Description.ToLower().Contains(term)));
        }

        return await items
            .OrderBy(item => item.Title)
            .ThenBy(item => item.Id)
            .Take(pageSize)
            .Select(item => new WorkItemSearchResultDto(item.Id, item.ProjectId, item.Title, item.Type, item.Status, item.Estimate, item.Version))
            .ToListAsync(cancellationToken);
    }
}
