using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanBoard.Tests;

// The endpoints that live in the API's Program.cs, started as the real application with SQLite in place of MariaDB:
// each one that returns work items must return their blockers, dependents and links.
public sealed class ProgramDependencyWiringTests
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

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

    [Fact]
    public async Task Board_update_move_and_comment_responses_carry_links()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "LINK" });
        var a = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        var b = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "B", type = "Task", status = "Backlog", priority = "Medium" });
        const string url = "https://github.com/acme/app/pull/42";

        // The link routes are mapped by Program.cs.
        var linked = await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/links", new AddWorkItemLinkRequest("pr", url));
        var link = Assert.Single(linked.Links);
        var board = (await client.GetFromJsonAsync<ProjectBoardDto>($"/api/projects/{project.Id}", Json))!.Items.ToDictionary(item => item.Id);
        var updated = await SendAsync<WorkItemDto>(client, HttpMethod.Put, $"/api/items/{b.Id}", new { description = "changed" });
        var moved = await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/move", new { status = "InProgress", order = 0 });
        var commented = await PostAsync<WorkItemDto>(client, $"/api/items/{b.Id}/comments", new { author = "tester", body = "note" });
        var found = await client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items/by-link?url={Uri.EscapeDataString(url)}", Json);
        var removed = await client.DeleteFromJsonAsync<WorkItemDto>($"/api/items/{b.Id}/links?linkId={link.Id}", Json);

        Assert.Equal(("pr", url), (link.Kind, link.Url));
        Assert.Empty(a.Links);
        Assert.Empty(board[a.Id].Links);
        Assert.Equal([link], board[b.Id].Links);
        Assert.Equal([link], updated.Links);
        Assert.Equal([link], moved.Links);
        Assert.Equal([link], commented.Links);
        Assert.Equal(b.Id, Assert.Single(found!).Id);
        Assert.Empty(removed!.Links);
    }

    [Fact]
    public async Task A_comment_stamps_its_item_before_it_inserts_the_comment()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "ORDER" });
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        api.Commands.Clear();

        await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/comments", new { author = "tester", body = "note" });

        // On MariaDB the other order deadlocks two writers of one item: each INSERT holds a shared lock on the
        // item's row (its foreign key) while the UPDATE of the other waits for it.
        var writes = api.Commands.Where(text => text.StartsWith("UPDATE", StringComparison.Ordinal) || text.StartsWith("INSERT", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, writes.Count);
        Assert.StartsWith("UPDATE \"WorkItems\"", writes[0], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO \"WorkItemComments\"", writes[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comment_routes_are_mapped_the_add_returns_the_comment_id_and_a_repeated_key_adds_nothing()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "CMT" });
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "A", type = "Task", status = "Backlog", priority = "Medium" });

        var first = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/comments", new { author = "tester", body = "note", idempotencyKey = "key" });
        api.Commands.Clear();
        var repeat = await PostAsync<WorkItemDto>(client, $"/api/items/{item.Id}/comments", new { author = "tester", body = "note", idempotencyKey = "key" });
        var writes = api.Commands.Where(text => text.StartsWith("UPDATE", StringComparison.Ordinal) || text.StartsWith("INSERT", StringComparison.Ordinal)).ToList();
        var page = await client.GetFromJsonAsync<WorkItemCommentPageDto>($"/api/items/{item.Id}/comments?limit=1", Json);

        var comment = Assert.Single(page!.Items);
        Assert.Equal((comment.Id, true), (first.CommentId, first.CommentCreated));
        Assert.Equal((comment.Id, false), (repeat.CommentId, repeat.CommentCreated));
        Assert.Equal(("tester", "note", first.UpdatedAtUtc), (comment.Author, comment.Body, comment.CreatedAtUtc));
        Assert.Equal(first.UpdatedAtUtc, repeat.UpdatedAtUtc);
        // The repeat takes the item's row first, like every comment write, and then gives the stamp back.
        Assert.StartsWith("UPDATE \"WorkItems\"", Assert.Single(writes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_on_an_item_deleted_after_it_was_loaded_is_refused_not_found()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        var project = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "GONE" });
        var item = await PostAsync<WorkItemDto>(client, "/api/items", new { projectId = project.Id, title = "A", type = "Task", status = "Backlog", priority = "Medium" });
        // The delete lands just before the endpoint opens its transaction.
        var deleted = 0;
        api.BeforeNextTransaction = () => deleted = api.DeleteItem(item.Id);

        using var response = await client.PostAsJsonAsync($"/api/items/{item.Id}/comments", new { author = "tester", body = "note" });

        Assert.Equal(1, deleted);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Project_routes_are_mapped_a_repeated_key_returns_the_same_project_and_an_update_changes_only_what_it_names()
    {
        using var api = new ApiApplication();
        using var client = api.CreateClient();
        const string repository = "git@github.com:acme/app.git";

        var created = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "TGT", repositoryUrl = repository, idempotencyKey = "plan-1" });
        var repeat = await PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "Project", key = "TGT", repositoryUrl = repository, idempotencyKey = "plan-1" });
        var branched = await SendAsync<ProjectSummaryDto>(client, HttpMethod.Patch, $"/api/projects/{created.Id}", new { defaultBranch = "main" });
        var put = await SendAsync<ProjectSummaryDto>(client, HttpMethod.Put, $"/api/projects/{created.Id}", new { name = "Renamed", key = "TGT", description = (string?)null, isArchived = false });
        var cleared = await SendAsync<ProjectSummaryDto>(client, HttpMethod.Patch, $"/api/projects/{created.Id}", new { defaultBranch = (string?)null, isArchived = true });
        var listed = await client.GetFromJsonAsync<List<ProjectSummaryDto>>("/api/projects?includeArchived=true", Json);
        var board = await client.GetFromJsonAsync<ProjectBoardDto>($"/api/projects/{created.Id}", Json);
        using var unknown = await client.PatchAsJsonAsync($"/api/projects/{Guid.NewGuid()}", new { name = "x" });

        Assert.Equal((true, repository, null), (created.Created, created.RepositoryUrl, created.DefaultBranch));
        Assert.Equal((created.Id, false), (repeat.Id, repeat.Created));
        Assert.Equal((repository, "main"), (branched.RepositoryUrl, branched.DefaultBranch));
        Assert.Equal(("Renamed", repository, "main"), (put.Name, put.RepositoryUrl, put.DefaultBranch));
        Assert.Equal(("Renamed", repository, null, true), (cleared.Name, cleared.RepositoryUrl, cleared.DefaultBranch, cleared.IsArchived));
        Assert.Equal(cleared, Assert.Single(listed!));
        // An archived project still has a board, as when it is archived from the web UI.
        Assert.True(board!.IsArchived);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(KanbanErrorCodes.NotFound, (await unknown.Content.ReadFromJsonAsync<KanbanErrorDto>())!.Code);
    }

    internal static Task<T> PostAsync<T>(HttpClient client, string path, object body) => SendAsync<T>(client, HttpMethod.Post, path, body);

    internal static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string path, object body)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) });
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    // Any type of the API assembly names it; the test project also references the MCP server, which has its own Program.
    internal sealed class ApiApplication : WebApplicationFactory<KanbanDbContext>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kanban-program-{Guid.NewGuid():N}.db");
        private readonly CommandRecorder recorder = new();
        private readonly TransactionHook transactionHook = new();

        /// <summary>The text of every SQL command the application has run, in order.</summary>
        public List<string> Commands => recorder.Commands;

        /// <summary>Runs once, just before the application opens its next transaction.</summary>
        public Action? BeforeNextTransaction
        {
            set => transactionHook.BeforeNext = value;
        }

        /// <summary>Deletes the item behind the application's back; returns the number of rows deleted.</summary>
        public int DeleteItem(Guid itemId)
        {
            using var dbContext = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options);
            return dbContext.WorkItems.Where(item => item.Id == itemId).ExecuteDelete();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            using (var dbContext = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options))
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

                services.AddDbContext<KanbanDbContext>(options => options.UseSqlite($"Data Source={databasePath};Pooling=False").AddInterceptors(recorder, transactionHook));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            File.Delete(databasePath);
        }
    }

    private sealed class TransactionHook : DbTransactionInterceptor
    {
        public Action? BeforeNext { get; set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            var action = BeforeNext;
            BeforeNext = null;
            action?.Invoke();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
