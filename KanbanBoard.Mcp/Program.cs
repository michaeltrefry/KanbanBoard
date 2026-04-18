using KanbanBoard.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);

var apiBaseUrl = builder.Configuration["KANBAN_API_BASE_URL"]
    ?? (builder.Environment.IsDevelopment()
        ? "https://localhost:7256"
        : "http://api:8080");

builder.Services.AddHttpClient("kanban-api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<KanbanTools>();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/mcp"));
app.MapMcp("/mcp");

app.Run();
