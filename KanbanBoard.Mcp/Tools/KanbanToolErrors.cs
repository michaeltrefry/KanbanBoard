using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using KanbanBoard.Shared.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KanbanBoard.Mcp.Tools;

/// <summary>
/// A refused tool call. <see cref="Code"/> is one of <see cref="KanbanErrorCodes"/>; callers branch on it, never on the message.
/// </summary>
public sealed class KanbanToolException(string code, string message) : McpException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The one error envelope for every tool. A refused call returns a tool result with <c>isError: true</c> whose
/// <c>structuredContent</c> is <c>{"code": "...", "message": "..."}</c> and whose single text block is that same JSON.
/// </summary>
public static class KanbanToolErrors
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static KanbanToolException ScopeDenied(string message) => new(KanbanErrorCodes.ScopeDenied, message);

    public static KanbanToolException InvalidArgument(string message) => new(KanbanErrorCodes.InvalidArgument, message);

    public static KanbanToolException NotFound(string message) => new(KanbanErrorCodes.NotFound, message);

    /// <summary>
    /// Throws a <see cref="KanbanToolException"/> when the API refused the call (400, 403, 404, 409, or 503 with an error body), using the
    /// API's own code when it sent one. Other failures throw as before: they are faults, not refusals.
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var fallbackCode = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => KanbanErrorCodes.InvalidArgument,
            HttpStatusCode.Forbidden => KanbanErrorCodes.ScopeDenied,
            HttpStatusCode.NotFound => KanbanErrorCodes.NotFound,
            HttpStatusCode.Conflict => KanbanErrorCodes.Conflict,
            _ => null
        };
        // A 503 is a refusal only when the API wrote the body: a proxy answers 503 as well.
        if (fallbackCode is null && response.StatusCode != HttpStatusCode.ServiceUnavailable)
        {
            response.EnsureSuccessStatusCode();
        }

        KanbanErrorDto? error = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            error = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<KanbanErrorDto>(body, JsonOptions);
        }
        catch (JsonException)
        {
            // Not an error body the API wrote; fall back to the status code.
        }

        if (fallbackCode is null && string.IsNullOrWhiteSpace(error?.Code))
        {
            response.EnsureSuccessStatusCode();
        }

        throw new KanbanToolException(
            string.IsNullOrWhiteSpace(error?.Code) ? fallbackCode! : error.Code,
            string.IsNullOrWhiteSpace(error?.Message)
                ? $"The Kanban API refused the call with status {(int)response.StatusCode}."
                : error.Message);
    }

    public static CallToolResult ToCallToolResult(KanbanToolException exception)
    {
        var envelope = new JsonObject
        {
            ["code"] = exception.Code,
            ["message"] = exception.Message
        };

        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(envelope),
            Content = [new TextContentBlock { Text = envelope.ToJsonString() }]
        };
    }

    public static IMcpServerBuilder WithKanbanToolErrors(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            catch (KanbanToolException exception)
            {
                return ToCallToolResult(exception);
            }
        }));
}
