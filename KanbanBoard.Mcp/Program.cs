using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Mcp.Services;
using KanbanBoard.Mcp.Tools;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var internalApiOptions = builder.Configuration
    .GetSection(InternalApiOptions.SectionName)
    .Get<InternalApiOptions>() ?? new InternalApiOptions();
var mcpAuthenticationOptions = builder.Configuration
    .GetSection(McpAuthenticationOptions.SectionName)
    .Get<McpAuthenticationOptions>() ?? new McpAuthenticationOptions();
var configurationErrors = internalApiOptions
    .Validate(required: mcpAuthenticationOptions.RequirePersonalAccessToken)
    .Concat(mcpAuthenticationOptions.Validate())
    .ToList();

if (configurationErrors.Count > 0)
{
    throw new InvalidOperationException(
        "MCP authentication configuration is invalid: " + string.Join(" ", configurationErrors));
}

var apiBaseUrl = builder.Configuration["KANBAN_API_BASE_URL"]
    ?? (builder.Environment.IsDevelopment()
        ? "https://localhost:7256"
        : "http://api:8080");

builder.Services.Configure<InternalApiOptions>(builder.Configuration.GetSection(InternalApiOptions.SectionName));
builder.Services.Configure<McpAuthenticationOptions>(builder.Configuration.GetSection(McpAuthenticationOptions.SectionName));
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddTransient<InternalApiAuthenticationHandler>();
builder.Services.AddSingleton<IMcpPersonalAccessTokenValidator, McpPersonalAccessTokenValidator>();
builder.Services.AddHttpClient("kanban-api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
})
.AddHttpMessageHandler<InternalApiAuthenticationHandler>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<KanbanTools>();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/mcp"));
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase),
    branch =>
    {
        branch.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<McpAuthenticationOptions>>().Value;
            if (!options.RequirePersonalAccessToken)
            {
                await next(context);
                return;
            }

            if (!TryGetBearerToken(context.Request, out var token))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var validator = context.RequestServices.GetRequiredService<IMcpPersonalAccessTokenValidator>();
            var authenticatedToken = await validator.ValidateAsync(token, context.RequestAborted);
            if (authenticatedToken is null)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Items[typeof(McpAuthenticatedToken)] = authenticatedToken;
            await next(context);
        });
    });

app.MapMcp("/mcp");

app.Run();

static bool TryGetBearerToken(HttpRequest request, out string token)
{
    token = string.Empty;
    if (!request.Headers.TryGetValue("Authorization", out var authorizationHeaders) ||
        authorizationHeaders.Count != 1)
    {
        return false;
    }

    var authorizationHeader = authorizationHeaders[0];
    if (authorizationHeader is null ||
        !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    token = authorizationHeader["Bearer ".Length..].Trim();
    return !string.IsNullOrWhiteSpace(token);
}
