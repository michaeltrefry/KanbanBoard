using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Api.Services;

public sealed class UpdateWorkItemPatch
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public bool HasEpicId { get; init; }
    public Guid? EpicId { get; init; }
    public bool HasTitle { get; init; }
    public string? Title { get; init; }
    public bool HasDescription { get; init; }
    public string? Description { get; init; }
    public bool HasType { get; init; }
    public WorkItemType? Type { get; init; }
    public bool HasStatus { get; init; }
    public WorkItemStatus? Status { get; init; }
    public bool HasPriority { get; init; }
    public WorkItemPriority? Priority { get; init; }
    public bool HasEstimate { get; init; }
    public int? Estimate { get; init; }
    public bool HasLabels { get; init; }
    public string? Labels { get; init; }

    public static bool TryParse(JsonElement payload, out UpdateWorkItemPatch? patch, out string? error)
    {
        patch = null;
        error = null;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Request body must be a JSON object.";
            return false;
        }

        if (!TryReadGuid(payload, "epicId", out var hasEpicId, out var epicId, out error)
            || !TryReadString(payload, "title", out var hasTitle, out var title, out error)
            || !TryReadString(payload, "description", out var hasDescription, out var description, out error)
            || !TryReadEnum<WorkItemType>(payload, "type", out var hasType, out var type, out error)
            || !TryReadEnum<WorkItemStatus>(payload, "status", out var hasStatus, out var status, out error)
            || !TryReadEnum<WorkItemPriority>(payload, "priority", out var hasPriority, out var priority, out error)
            || !TryReadInt(payload, "estimate", out var hasEstimate, out var estimate, out error)
            || !TryReadString(payload, "labels", out var hasLabels, out var labels, out error))
        {
            return false;
        }

        patch = new UpdateWorkItemPatch
        {
            HasEpicId = hasEpicId,
            EpicId = epicId,
            HasTitle = hasTitle,
            Title = title,
            HasDescription = hasDescription,
            Description = description,
            HasType = hasType,
            Type = type,
            HasStatus = hasStatus,
            Status = status,
            HasPriority = hasPriority,
            Priority = priority,
            HasEstimate = hasEstimate,
            Estimate = estimate,
            HasLabels = hasLabels,
            Labels = labels
        };

        return true;
    }

    private static bool TryReadGuid(JsonElement payload, string propertyName, out bool hasValue, out Guid? value, out string? error)
    {
        error = null;
        value = null;
        hasValue = payload.TryGetProperty(propertyName, out var property);

        if (!hasValue)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        try
        {
            value = property.Deserialize<Guid>(JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            error = $"Field '{propertyName}' is invalid.";
            return false;
        }
    }

    private static bool TryReadInt(JsonElement payload, string propertyName, out bool hasValue, out int? value, out string? error)
    {
        error = null;
        value = null;
        hasValue = payload.TryGetProperty(propertyName, out var property);

        if (!hasValue)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        try
        {
            value = property.Deserialize<int>(JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            error = $"Field '{propertyName}' is invalid.";
            return false;
        }
    }

    private static bool TryReadEnum<TEnum>(JsonElement payload, string propertyName, out bool hasValue, out TEnum? value, out string? error)
        where TEnum : struct
    {
        error = null;
        value = null;
        hasValue = payload.TryGetProperty(propertyName, out var property);

        if (!hasValue)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        try
        {
            value = property.Deserialize<TEnum>(JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            error = $"Field '{propertyName}' is invalid.";
            return false;
        }
    }

    private static bool TryReadString(JsonElement payload, string propertyName, out bool hasValue, out string? value, out string? error)
    {
        error = null;
        value = null;
        hasValue = payload.TryGetProperty(propertyName, out var property);

        if (!hasValue)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"Field '{propertyName}' is invalid.";
            return false;
        }

        value = property.GetString();
        return true;
    }
}
