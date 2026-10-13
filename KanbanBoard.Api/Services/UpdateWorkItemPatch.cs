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
    /// <summary>The labels when they were sent as one comma-separated string.</summary>
    public string? Labels { get; init; }
    /// <summary>The labels when they were sent as a list.</summary>
    public IReadOnlyList<string>? LabelList { get; init; }
    /// <summary>When set, the update is refused unless the item still has this version.</summary>
    public long? ExpectedVersion { get; init; }

    private static readonly string[] Fields =
        ["epicId", "title", "description", "type", "status", "priority", "estimate", "labels", "expectedVersion"];

    /// <summary>
    /// Reads the body of an update: a JSON object in which a field that is present is written, a field that is null
    /// is cleared, and a field that is absent is left alone. With <paramref name="strict"/>, a field a work item
    /// does not have is refused, so a misspelt one is not silently ignored.
    /// </summary>
    public static bool TryParse(JsonElement payload, bool strict, out UpdateWorkItemPatch? patch, out string? error)
    {
        patch = null;
        error = null;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Request body must be a JSON object.";
            return false;
        }

        if (strict && payload.EnumerateObject().Select(property => property.Name).FirstOrDefault(name => !Fields.Contains(name)) is { } unknown)
        {
            error = $"A work item has no field '{unknown}'. The fields are {string.Join(", ", Fields)}.";
            return false;
        }

        if (!TryReadGuid(payload, "epicId", out var hasEpicId, out var epicId, out error)
            || !TryReadString(payload, "title", out var hasTitle, out var title, out error)
            || !TryReadString(payload, "description", out var hasDescription, out var description, out error)
            || !TryReadEnum<WorkItemType>(payload, "type", out var hasType, out var type, out error)
            || !TryReadEnum<WorkItemStatus>(payload, "status", out var hasStatus, out var status, out error)
            || !TryReadEnum<WorkItemPriority>(payload, "priority", out var hasPriority, out var priority, out error)
            || !TryReadInt(payload, "estimate", out var hasEstimate, out var estimate, out error)
            || !TryReadLabels(payload, out var hasLabels, out var labels, out var labelList, out error)
            || !TryReadVersion(payload, out var expectedVersion, out error))
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
            Labels = labels,
            LabelList = labelList,
            ExpectedVersion = expectedVersion
        };

        return true;
    }

    // A string of comma-separated labels (the web UI), a list of labels, or null.
    private static bool TryReadLabels(JsonElement payload, out bool hasValue, out string? labels, out IReadOnlyList<string>? labelList, out string? error)
    {
        labelList = null;
        if (!payload.TryGetProperty("labels", out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return TryReadString(payload, "labels", out hasValue, out labels, out error);
        }

        hasValue = true;
        labels = null;
        error = null;
        if (property.EnumerateArray().Any(label => label.ValueKind != JsonValueKind.String))
        {
            error = "Field 'labels' must be a string, a list of strings or null.";
            return false;
        }

        labelList = property.EnumerateArray().Select(label => label.GetString()!).ToList();
        return true;
    }

    private static bool TryReadVersion(JsonElement payload, out long? version, out string? error)
    {
        error = null;
        version = null;
        if (!payload.TryGetProperty("expectedVersion", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
        {
            version = value;
            return true;
        }

        error = "Argument 'expected_version' must be a whole number: the version of the item as it was last read.";
        return false;
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
