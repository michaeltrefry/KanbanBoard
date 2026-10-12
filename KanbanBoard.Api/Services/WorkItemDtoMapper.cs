using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Api.Services;

public static class WorkItemDtoMapper
{
    // A claim past its expiry is reported as no claim, so readers never have to compare clocks with the server.
    public static WorkItemDto ToDto(WorkItem item, DateTimeOffset now)
    {
        var claimed = item.ClaimExpiresAtUtc > now;
        return new(
            item.Id,
            item.ProjectId,
            item.EpicId,
            item.Epic is null ? null : new EpicReferenceDto(item.Epic.Id, item.Epic.Name),
            item.Title,
            item.Description,
            item.Type,
            item.Status,
            item.Priority,
            item.Order,
            item.Estimate,
            item.Labels,
            item.Comments
                .OrderBy(comment => comment.CreatedAtUtc)
                .ThenBy(comment => comment.Id)
                .Select(ToCommentDto)
                .ToList(),
            item.CreatedAtUtc,
            item.UpdatedAtUtc,
            claimed ? item.ClaimantName : null,
            claimed ? item.ClaimantUserId : null,
            claimed ? item.ClaimMarker : null,
            claimed ? item.ClaimExpiresAtUtc : null);
    }

    private static WorkItemCommentDto ToCommentDto(WorkItemComment comment) =>
        new(
            comment.Id,
            comment.WorkItemId,
            comment.Author,
            comment.Body,
            comment.CreatedAtUtc);
}
