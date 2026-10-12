using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanBoard.Api.Data;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanBoard.Tests;

// The endpoints that live in the API's Program.cs, started as the real application with SQLite in place of MariaDB:
// each one that returns work items must return their blockers and dependents.
public sealed class ProgramDependencyWiringTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Board_update_move_and_comment_responses_carry_blockers_and_dependents()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "WIRE" });
        var a = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "B", type = "Task", status = "Backlog", priority = "Medium" });
        await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/dependencies", new AddWorkItemDependencyRequest(a.Id));
        WorkItemDependencyRefDto[] blockedByA = [new(a.Id, "A", WorkItemStatus.Backlog)];

        var board = (await client.GetFromJsonAsync<ProjectBoardDto>($"/api/projects/{project.Id}", Json))!.Items.ToDictionary(item => item.Id);
        var updated = await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{b.Id}", new { description = "changed" });
        var moved = await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/move", new { status = "InProgress", order = 0 });
        var commented = await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/comments", new { author = "tester", body = "note" });

        Assert.Equal(blockedByA, board[b.Id].Blockers);
        Assert.Equal([new WorkItemDependencyRefDto(b.Id, "B", WorkItemStatus.Backlog)], board[a.Id].Dependents);
        Assert.Equal("changed", updated.Description);
        Assert.Equal(blockedByA, updated.Blockers);
        Assert.Equal(WorkItemStatus.InProgress, moved.Status);
        Assert.Equal(blockedByA, moved.Blockers);
        Assert.Single(commented.Comments);
        Assert.Equal(blockedByA, commented.Blockers);
    }

    private static Task<T> PostAsync<T>(HttpClient client, string path, object body) => SendAsync<T>(client, HttpMethod.Post, path, body);

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string path, object body)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) });
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    // Any type of the API assembly names it; the test project also references the MCP server, which has its own Program.
    private sealed class ApiApplication : WebApplicationFactory<KanbanDbContext>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kanban-program-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            using (var dbContext = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlite($"Data Source={databasePath}").Options))
            {
                dbContext.Database.EnsureCreated();
            }

            // Program.cs validates the connection string before it builds; it is never used to connect.
            builder.UseSetting("ConnectionStrings:Kanban", "Server=unused;Database=unused");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Auth:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                foreach (var registration in services
                    .Where(service => service.ServiceType.IsGenericType &&
                        service.ServiceType.GenericTypeArguments.Contains(typeof(KanbanDbContext)))
                    .ToList())
                {
                    services.Remove(registration);
                }

                services.AddDbContext<KanbanDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }
}
