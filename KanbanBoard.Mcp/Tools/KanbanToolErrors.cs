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
/// A call whose arguments cannot be read as the tool's parameters, or that passes an argument the tool does not declare,
/// is refused the same way, with <c>invalid_argument</c>, before the tool runs.
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

    // The call the current request is making, set by the filter below before the tool is invoked.
    private static readonly AsyncLocal<ToolCall?> CurrentCall = new();

    private sealed class ToolCall
    {
        public bool BodyEntered;
    }

    /// <summary>
    /// Says that the body of a tool has started to run. Every tool calls it first, before it does anything else:
    /// what the server throws before that is a fault of the arguments, what is thrown after it is not.
    /// </summary>
    public static void ToolBodyEntered()
    {
        if (CurrentCall.Value is { } call)
        {
            call.BodyEntered = true;
        }
    }

    // An argument that could not be turned into the tool's parameter: a required one that is missing, or JSON of
    // another type or shape (a string where a list of plan items belongs). The MCP server throws these before the
    // body of the tool runs, so nothing was called and nothing was written. The same exceptions thrown once the body
    // has started (while the tool runs, or while its result is written) are faults of the server, not of the caller,
    // and stay plain errors: the call may have written.
    private static bool IsMalformedArgument(Exception exception, ToolCall call) =>
        exception is JsonException or ArgumentException && !call.BodyEntered;

    // The first argument of the call that the tool does not declare, as a refusal; null when there is none. Such an
    // argument would otherwise be dropped without a word: a misspelt expected_version would turn a guarded write
    // into an unguarded one, a misspelt documents would import a plan without its documents.
    private static KanbanToolException? UndeclaredArgument(RequestContext<CallToolRequestParams> request)
    {
        if (request.MatchedPrimitive is not McpServerTool tool ||
            request.Params?.Arguments is not { Count: > 0 } arguments ||
            !tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var declared = properties.EnumerateObject().Select(property => property.Name).ToList();
        var undeclared = arguments.Keys.FirstOrDefault(name => !declared.Contains(name, StringComparer.Ordinal));
        return undeclared is null
            ? null
            : InvalidArgument(
                $"{tool.ProtocolTool.Name} has no argument '{undeclared}'. Its arguments are: {string.Join(", ", declared)}. Argument names are compared exactly. Nothing was called.");
    }

    public static IMcpServerBuilder WithKanbanToolErrors(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            if (UndeclaredArgument(request) is { } undeclared)
            {
                return ToCallToolResult(undeclared);
            }

            var call = new ToolCall();
            CurrentCall.Value = call;
            try
            {
                return await next(request, cancellationToken);
            }
            catch (KanbanToolException exception)
            {
                return ToCallToolResult(exception);
            }
            catch (Exception exception) when (IsMalformedArgument(exception, call))
            {
                return ToCallToolResult(InvalidArgument(
                    $"An argument of {request.Params?.Name} is missing or is not of the type the tool takes: {exception.Message}"));
            }
        }));
}
