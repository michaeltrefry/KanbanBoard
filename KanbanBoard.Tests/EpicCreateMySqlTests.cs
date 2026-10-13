using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The creates of an epic and of an epic document without an idempotency key, when the parent is deleted between
// the check that it exists and the INSERT: MariaDB refuses the INSERT on the foreign key (error 1452), which the
// SQLite tests never see. The provider runs for real; FakeDatabase records each command and answers it.
public sealed class EpicCreateMySqlTests
{
    private static readonly Guid ParentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Each create: its path, its body, the table it inserts into, the foreign key to its parent and its refusal.
    public static TheoryData<string, string, string, string> Creates => new()
    {
        { $"/api/projects/{ParentId}/epics", "Epics", "FK_Epics_Projects_ProjectId", "Project not found." },
        { $"/api/epics/{ParentId}/documents", "EpicDocuments", "FK_EpicDocuments_Epics_EpicId", "Epic not found." }
    };

    [Theory]
    [MemberData(nameof(Creates))]
    public async Task A_create_whose_parent_is_deleted_before_the_insert_is_refused_not_found_like_an_unknown_parent(string path, string table, string foreignKey, string message)
    {
        var parentExists = true;
        var database = Database(() => parentExists, table, MissingParent(table, foreignKey));
        using var api = new WorkItemDeadlockRetryMySqlTests.FakeBackedApplication(database);
        using var client = api.CreateClient();
        var body = new { name = "Epic", title = "Doc", body = "b" };

        using var response = await client.PostAsJsonAsync(path, body);
        var inserts = database.Commands.Count(command => command.Text.StartsWith($"INSERT INTO `{table}`", StringComparison.Ordinal));
        parentExists = false;
        using var unknown = await client.PostAsJsonAsync(path, body);

        // The parent was there when the create looked, and the INSERT was sent and refused.
        Assert.Equal(1, inserts);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(new KanbanErrorDto(KanbanErrorCodes.NotFound, message), await response.Content.ReadFromJsonAsync<KanbanErrorDto>());
        // The same body as for a parent that is not there when the create looks.
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
    }

    // Only the foreign key to the parent says that the parent is gone: any other refusal of the INSERT stays a fault.
    [Theory]
    [InlineData(1452, "FK_Epics_Other_OtherId")]
    [InlineData(1451, "FK_Epics_Projects_ProjectId")]
    public async Task A_create_of_an_epic_that_fails_on_anything_but_the_foreign_key_to_its_project_is_not_classified(int number, string foreignKey)
    {
        var database = Database(() => true, "Epics", ServerError(Message("Epics", foreignKey), number));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var create = () => Epics.CreateAsync(dbContext, new Epic { ProjectId = ParentId, Name = "Epic" }, null, CancellationToken.None);

        Assert.IsType<MySqlException>((await Assert.ThrowsAsync<DbUpdateException>(create)).InnerException);
    }

    [Theory]
    [InlineData(1452, "FK_EpicDocuments_Other_OtherId")]
    [InlineData(1451, "FK_EpicDocuments_Epics_EpicId")]
    public async Task A_create_of_a_document_that_fails_on_anything_but_the_foreign_key_to_its_epic_is_not_classified(int number, string foreignKey)
    {
        var database = Database(() => true, "EpicDocuments", ServerError(Message("EpicDocuments", foreignKey), number));
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var create = () => Epics.CreateDocumentAsync(dbContext, new EpicDocument { EpicId = ParentId, Title = "Doc", Body = "b" }, null, CancellationToken.None);

        Assert.IsType<MySqlException>((await Assert.ThrowsAsync<DbUpdateException>(create)).InnerException);
    }

    // "Does the parent exist?" is answered by parentExists; the INSERT into the table fails.
    private static FakeDatabase Database(Func<bool> parentExists, string table, Exception failure) =>
        new(lockResult: 1, command =>
        {
            if (!command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal))
            {
                return null;
            }

            var answer = new DataTable();
            answer.Columns.Add("e", typeof(bool));
            answer.Rows.Add(parentExists());
            return answer;
        })
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.StartsWith($"INSERT INTO `{table}`", StringComparison.Ordinal) ? failure : null
        };

    private static Exception MissingParent(string table, string foreignKey) => ServerError(Message(table, foreignKey), 1452);

    // The message of MariaDB for error 1452.
    private static string Message(string table, string foreignKey) =>
        $"Cannot add or update a child row: a foreign key constraint fails (`kanban`.`{table}`, CONSTRAINT `{foreignKey}` FOREIGN KEY (`ParentId`) REFERENCES `Parents` (`Id`) ON DELETE CASCADE)";

    // MySqlException carries the server's error number only through an internal constructor.
    private static Exception ServerError(string message, int number) =>
        (Exception)Activator.CreateInstance(
            typeof(MySqlException),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [message, number],
            culture: null)!;
}
