using System.Data;
using System.Data.Common;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Data.Migrations;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MySql.Data.MySqlClient;
using static KanbanBoard.Tests.StartupMigrationTests;

namespace KanbanBoard.Tests;

// The SQL that MySql.EntityFrameworkCore writes for the import of an epic plan and for the keyed creates, and what
// they do when MariaDB refuses a statement (errors 1062 and 1213), which the SQLite tests never see. The provider
// runs for real; FakeDatabase records each command and answers it.
public sealed class EpicPlanImportMySqlTests
{
    private const string LockProject = "UPDATE `Projects` AS `p`\nSET `IsArchived` = `p`.`IsArchived`\nWHERE `p`.`Id` = @projectId";
    private const string LockEpic = "UPDATE `Epics` AS `e`\nSET `IsArchived` = `e`.`IsArchived`\nWHERE `e`.`Id` = @document_EpicId";
    private const string NextOrder = "SELECT MAX(`w`.`Order`)\nFROM `WorkItems` AS `w`\nWHERE (`w`.`ProjectId` = @projectId) AND (`w`.`Status` = @status)";
    private const string KeyHash = "hash";
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EpicId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ExistingId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_import_holds_the_project_looks_for_the_key_reads_the_column_ends_inserts_everything_and_records_the_entries_last()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var plan = await PlanAsync();

        var result = await EpicPlans.ImportAsync(dbContext, plan, KeyHash, null, Now, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Created, plan.Epic.Id), result);
        var texts = Texts(database);
        Assert.True(texts.Count == 8, string.Join("\n---\n", texts));
        // The UPDATE comes first: it is what makes two imports into one project run one after the other, and what
        // lets the reads that follow see what the import before this one committed.
        Assert.Equal(LockProject, texts[0]);
        Assert.Equal(
            "SELECT `e`.`EpicId`\nFROM `EpicPlanImports` AS `e`\nWHERE (`e`.`ProjectId` = @projectId) AND (`e`.`IdempotencyKeyHash` = @idempotencyKeyHash)\nLIMIT 1",
            texts[1]);
        // One read of the end of a column per status of the plan.
        Assert.Equal([NextOrder, NextOrder], texts[2..4]);
        // One command for the rows, parents before children: the epic, then what refers to it, then the dependencies
        // after the items they name.
        var inserts = texts[4];
        int At(string table) => inserts.IndexOf($"INSERT INTO `{table}`", StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO `Epics` (`Id`, `CreatedAtUtc`, `Description`, `IdempotencyKeyHash`, `IsArchived`, `Name`, `ProjectId`, `UpdatedAtUtc`)", inserts, StringComparison.Ordinal);
        Assert.True(At("EpicDocuments") > 0 && At("EpicPlanImports") > 0 && At("WorkItems") > 0, inserts);
        Assert.True(At("WorkItemDependencies") > At("WorkItems"), inserts);
        Assert.Contains("INSERT INTO `EpicPlanImports` (`EpicId`, `IdempotencyKeyHash`, `ProjectId`, `Result`)", inserts, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO `WorkItemDependencies` (`BlockedItemId`, `BlockerItemId`)", inserts, StringComparison.Ordinal);
        Assert.Equal(2, plan.Items.Select(planned => planned.Item.Order).Count(order => order == 5));
        Assert.Equal([5, 5, 6], plan.Items.Select(planned => planned.Item.Order).ToArray());
        // The change log lock is taken after every row is written, for the entries and the commit only: the items
        // and one dependency (three items, one dependency).
        Assert.Contains("GET_LOCK(", texts[5], StringComparison.Ordinal);
        Assert.Equal(4, texts[6].Split("INSERT INTO `WorkItemChanges`").Length - 1);
        Assert.Contains("RELEASE_LOCK(", texts[7], StringComparison.Ordinal);
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Fact]
    public async Task An_import_whose_key_has_an_import_reports_its_epic_and_writes_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: () => true)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await EpicPlans.ImportAsync(dbContext, await PlanAsync(), KeyHash, null, Now, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Existing, ExistingId), result);
        var texts = Texts(database);
        Assert.True(texts.Count == 2, string.Join("\n---\n", texts));
        Assert.Equal(LockProject, texts[0]);
        Assert.DoesNotContain("COMMIT", database.Locks);
    }

    [Fact]
    public async Task An_import_into_a_project_the_update_does_not_match_is_not_found_and_reads_nothing_more()
    {
        var database = new FakeDatabase(lockResult: 1, Answer());
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await EpicPlans.ImportAsync(dbContext, await PlanAsync(), KeyHash, null, Now, CancellationToken.None);

        Assert.Equal(KeyedCreateOutcome.NotFound, result.Outcome);
        Assert.Equal(LockProject, Assert.Single(Texts(database)));
    }

    // Should the unique index refuse the INSERT although the project's row was held, the import that won is not
    // visible inside this transaction (REPEATABLE READ): the key is looked up again once the transaction is gone.
    [Fact]
    public async Task An_import_that_loses_its_key_to_a_concurrent_import_reports_that_import_and_records_nothing()
    {
        var lost = false;
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: () => lost))
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.StartsWith("INSERT INTO `Epics`", StringComparison.Ordinal)
                ? Lost(ref lost, $"Duplicate entry '{ProjectId}-hash' for key '{EpicPlans.IdempotencyIndexName}'")
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await EpicPlans.ImportAsync(dbContext, await PlanAsync(), KeyHash, null, Now, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Existing, ExistingId), result);
        var texts = Texts(database);
        Assert.StartsWith("SELECT `e`.`EpicId`\nFROM `EpicPlanImports`", texts[^1], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO `Epics`", texts[^2], StringComparison.Ordinal);
        Assert.DoesNotContain(texts, text => text.Contains("WorkItemChanges", StringComparison.Ordinal) || text.Contains("_LOCK(", StringComparison.Ordinal));
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task A_duplicate_entry_on_another_index_is_not_taken_for_a_repeated_key()
    {
        var lost = false;
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: () => lost))
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.StartsWith("INSERT INTO `Epics`", StringComparison.Ordinal)
                ? Lost(ref lost, "Duplicate entry 'x' for key 'PRIMARY'")
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var plan = await PlanAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => EpicPlans.ImportAsync(dbContext, plan, KeyHash, null, Now, CancellationToken.None));

        Assert.StartsWith("INSERT INTO `Epics`", Texts(database)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_import_that_was_the_victim_of_a_deadlock_is_run_again_and_records_its_entries_once()
    {
        var deadlocks = 1;
        var database = new FakeDatabase(lockResult: 1, Answer())
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.StartsWith("INSERT INTO `Epics`", StringComparison.Ordinal) && deadlocks-- > 0
                ? ServerError("Deadlock found when trying to get lock; try restarting transaction", 1213)
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await EpicPlans.ImportAsync(dbContext, await PlanAsync(), KeyHash, null, Now, CancellationToken.None);

        Assert.Equal(KeyedCreateOutcome.Created, result.Outcome);
        var texts = Texts(database);
        Assert.Equal(2, texts.Count(text => text == LockProject));
        Assert.Equal(2, texts.Count(text => text.StartsWith("INSERT INTO `Epics`", StringComparison.Ordinal)));
        Assert.Single(texts, text => text.Contains("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal));
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Fact]
    public async Task An_import_that_deadlocks_three_times_is_busy_and_records_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer())
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.StartsWith("INSERT INTO `Epics`", StringComparison.Ordinal)
                ? ServerError("Deadlock found when trying to get lock; try restarting transaction", 1213)
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var plan = await PlanAsync();

        await Assert.ThrowsAsync<KanbanBusyException>(() => EpicPlans.ImportAsync(dbContext, plan, KeyHash, null, Now, CancellationToken.None));

        var texts = Texts(database);
        Assert.Equal(WorkItemChangeLog.DeadlockAttempts, texts.Count(text => text == LockProject));
        Assert.DoesNotContain(texts, text => text.Contains("WorkItemChanges", StringComparison.Ordinal));
        Assert.DoesNotContain("COMMIT", database.Locks);
    }

    [Fact]
    public async Task A_keyed_item_holds_the_project_looks_for_the_key_reads_the_column_end_and_inserts()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var item = new WorkItem { ProjectId = ProjectId, Title = "Item", Status = WorkItemStatus.Ready };

        var result = await WorkItemWrites.CreateAsync(dbContext, item, KeyHash, null, Now, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Created, item.Id), result);
        var texts = WithoutChangeLogEntry(Texts(database), readsProject: false);
        Assert.True(texts.Count == 4, string.Join("\n---\n", texts));
        Assert.Equal(LockProject, texts[0]);
        Assert.Equal(
            "SELECT `w`.`Id`\nFROM `WorkItems` AS `w`\nWHERE (`w`.`ProjectId` = @item_ProjectId) AND (`w`.`IdempotencyKeyHash` = @idempotencyKeyHash)\nLIMIT 1",
            texts[1]);
        Assert.Equal(NextOrder, texts[2]);
        Assert.StartsWith("INSERT INTO `WorkItems` (", texts[3], StringComparison.Ordinal);
        Assert.Contains("`IdempotencyKeyHash`", texts[3], StringComparison.Ordinal);
        Assert.Equal((KeyHash, 5), (item.IdempotencyKeyHash, item.Order));
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Fact]
    public async Task An_item_without_a_key_holds_the_project_before_it_reads_the_column_end_and_looks_for_no_key()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var item = new WorkItem { ProjectId = ProjectId, Title = "Item", Status = WorkItemStatus.Ready };

        var result = await WorkItemWrites.CreateAsync(dbContext, item, null, null, Now, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Created, item.Id), result);
        var texts = WithoutChangeLogEntry(Texts(database), readsProject: false);
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        // The same first statement as a keyed create and an import: the creates into one project run one at a time,
        // so the end of the column that is read next is the one this item is put at.
        Assert.Equal(LockProject, texts[0]);
        Assert.Equal(NextOrder, texts[1]);
        Assert.StartsWith("INSERT INTO `WorkItems` (", texts[2], StringComparison.Ordinal);
        Assert.Equal((null, 5), (item.IdempotencyKeyHash, item.Order));
        Assert.Equal([$"GET {WorkItemChangeLog.LockName}", $"RELEASE {WorkItemChangeLog.LockName}"], LocksAroundTheCommit(database));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public async Task An_item_without_a_key_that_deadlocks_is_created_again_once_or_refused_busy(int deadlocks, bool created)
    {
        var database = new FakeDatabase(lockResult: 1, Answer())
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.StartsWith("INSERT INTO `WorkItems`", StringComparison.Ordinal) && deadlocks-- > 0
                ? WorkItemDeadlockRetryMySqlTests.MySqlFailure(1213)
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var item = new WorkItem { ProjectId = ProjectId, Title = "Item", Status = WorkItemStatus.Ready };

        var create = () => WorkItemWrites.CreateAsync(dbContext, item, null, null, Now, CancellationToken.None);

        if (created)
        {
            Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Created, item.Id), await create());
        }
        else
        {
            await Assert.ThrowsAsync<KanbanBusyException>(create);
        }

        var texts = Texts(database);
        // Every attempt starts again from the statement that holds the project.
        Assert.Equal(created ? 2 : 3, texts.Count(text => text == LockProject));
        Assert.Equal(created ? 2 : 3, texts.Count(text => text.StartsWith("INSERT INTO `WorkItems`", StringComparison.Ordinal)));
        Assert.Equal(created ? 1 : 0, texts.Count(text => text.StartsWith("INSERT INTO `WorkItemChanges`", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_item_without_a_key_for_a_project_that_does_not_exist_is_not_found_and_reads_nothing()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 0 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await WorkItemWrites.CreateAsync(dbContext, new WorkItem { ProjectId = ProjectId, Title = "Item" }, null, null, Now, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.NotFound), result);
        Assert.Equal([LockProject], Texts(database));
    }

    [Fact]
    public async Task A_keyed_item_for_an_epic_checks_the_epic_after_the_key()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var item = new WorkItem { ProjectId = ProjectId, EpicId = EpicId, Title = "Item" };

        var result = await WorkItemWrites.CreateAsync(dbContext, item, KeyHash, null, Now, CancellationToken.None);

        // The fake has no such epic.
        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Invalid, Message: WorkItemWrites.EpicRefusal), result);
        var texts = Texts(database);
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.Contains("`IdempotencyKeyHash` = ", texts[1], StringComparison.Ordinal);
        Assert.Matches(@"^SELECT EXISTS \(\n    SELECT 1\n    FROM `Epics` AS `e`\n    WHERE \(\(`e`\.`Id` = @\w+\) AND \(`e`\.`ProjectId` = @\w+\)\) AND NOT \(`e`\.`IsArchived`\)\)$", texts[2]);
        Assert.DoesNotContain("COMMIT", database.Locks);
    }

    [Fact]
    public async Task A_keyed_epic_holds_the_project_looks_for_the_key_and_inserts()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var epic = new Epic { ProjectId = ProjectId, Name = "Epic" };

        var result = await Epics.CreateAsync(dbContext, epic, KeyHash, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Created, epic.Id), result);
        var texts = Texts(database);
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.Equal(LockProject, texts[0]);
        Assert.Equal(
            "SELECT `e`.`Id`\nFROM `Epics` AS `e`\nWHERE (`e`.`ProjectId` = @epic_ProjectId) AND (`e`.`IdempotencyKeyHash` = @idempotencyKeyHash)\nLIMIT 1",
            texts[1]);
        Assert.StartsWith("INSERT INTO `Epics` (", texts[2], StringComparison.Ordinal);
        Assert.Equal(["COMMIT"], database.Locks.Distinct().ToArray());
    }

    [Fact]
    public async Task A_keyed_document_holds_the_epic_looks_for_the_key_and_inserts()
    {
        var database = new FakeDatabase(lockResult: 1, Answer()) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);
        var document = new EpicDocument { EpicId = EpicId, Title = "Doc", Body = "b" };

        var result = await Epics.CreateDocumentAsync(dbContext, document, KeyHash, CancellationToken.None);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Created, document.Id), result);
        var texts = Texts(database);
        Assert.True(texts.Count == 3, string.Join("\n---\n", texts));
        Assert.Equal(LockEpic, texts[0]);
        Assert.Equal(
            "SELECT `e`.`Id`\nFROM `EpicDocuments` AS `e`\nWHERE (`e`.`EpicId` = @document_EpicId) AND (`e`.`IdempotencyKeyHash` = @idempotencyKeyHash)\nLIMIT 1",
            texts[1]);
        Assert.StartsWith("INSERT INTO `EpicDocuments` (", texts[2], StringComparison.Ordinal);
    }

    public static TheoryData<string> KeyedCreates => new() { "epic", "document", "item" };

    [Theory]
    [MemberData(nameof(KeyedCreates))]
    public async Task A_keyed_create_whose_key_has_an_object_reports_it_and_inserts_nothing(string kind)
    {
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: () => true)) { NonQueryResult = 1 };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await CreateAsync(dbContext, kind);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Existing, ExistingId), result);
        Assert.Equal(2, database.Commands.Count);
        Assert.DoesNotContain(database.Commands, command => command.Text.Contains("INSERT", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(KeyedCreates))]
    public async Task A_keyed_create_that_loses_its_key_to_a_concurrent_create_reports_the_winner(string kind)
    {
        var (table, index) = kind switch
        {
            "epic" => ("Epics", Epics.IdempotencyIndexName),
            "document" => ("EpicDocuments", Epics.DocumentIdempotencyIndexName),
            _ => ("WorkItems", WorkItemWrites.IdempotencyIndexName)
        };
        var lost = false;
        var database = new FakeDatabase(lockResult: 1, Answer(keyUsed: () => lost))
        {
            NonQueryResult = 1,
            Fail = command => command.CommandText.StartsWith($"INSERT INTO `{table}`", StringComparison.Ordinal)
                ? Lost(ref lost, $"Duplicate entry 'x-hash' for key '{index}'")
                : null
        };
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await CreateAsync(dbContext, kind);

        Assert.Equal(new KeyedCreateResult(KeyedCreateOutcome.Existing, ExistingId), result);
        var texts = Texts(database);
        Assert.Contains("`IdempotencyKeyHash` = ", texts[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(texts, text => text.Contains("WorkItemChanges", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(KeyedCreates))]
    public async Task A_keyed_create_in_a_parent_the_update_does_not_match_is_not_found(string kind)
    {
        var database = new FakeDatabase(lockResult: 1, Answer());
        await using var dbContext = CreatePipelineContext(new DatabaseOptions(), database);

        var result = await CreateAsync(dbContext, kind);

        Assert.Equal(KeyedCreateOutcome.NotFound, result.Outcome);
        Assert.StartsWith("UPDATE `", Assert.Single(Texts(database)), StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_and_deploy_script_add_the_same_columns_table_and_unique_indexes()
    {
        var migration = new AddCreateIdempotencyKeysAndEpicPlanImports();
        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToList();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        var indexes = migration.UpOperations.OfType<CreateIndexOperation>().ToList();
        var script = File.ReadAllText(FindRepoFile("deploy/sql/010_create_idempotency_keys_and_epic_plan_imports.sql")).ReplaceLineEndings("\n");

        // Existing rows keep theirs: every new column is nullable and nothing is altered or dropped.
        Assert.Equal(8, migration.UpOperations.Count);
        Assert.Equal(["WorkItems", "Epics", "EpicDocuments"], columns.Select(column => column.Table).ToArray());
        Assert.All(columns, column =>
        {
            Assert.Equal(("IdempotencyKeyHash", "varchar(64)", true), (column.Name, column.ColumnType, column.IsNullable));
            Assert.Contains($"ALTER TABLE `{column.Table}` ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash` varchar(64) NULL;", script, StringComparison.Ordinal);
        });

        Assert.Equal("EpicPlanImports", table.Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `EpicPlanImports` (", script, StringComparison.Ordinal);
        Assert.Equal(["EpicId", "ProjectId", "IdempotencyKeyHash", "Result"], table.Columns.Select(column => column.Name).ToArray());
        Assert.All(table.Columns, column =>
            Assert.Contains($"    `{column.Name}` {column.ColumnType} NOT NULL,", script, StringComparison.Ordinal));
        Assert.Equal(["EpicId"], table.PrimaryKey!.Columns);
        Assert.Contains("PRIMARY KEY (`EpicId`)", script, StringComparison.Ordinal);
        var foreignKey = Assert.Single(table.ForeignKeys);
        Assert.Equal(Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.Cascade, foreignKey.OnDelete);
        Assert.Contains($"CONSTRAINT `{foreignKey.Name}` FOREIGN KEY (`EpicId`) REFERENCES `Epics` (`Id`) ON DELETE CASCADE", script, StringComparison.Ordinal);

        Assert.Equal(
            [WorkItemWrites.IdempotencyIndexName, Epics.IdempotencyIndexName, Epics.DocumentIdempotencyIndexName, EpicPlans.IdempotencyIndexName],
            indexes.Select(index => index.Name).ToArray());
        Assert.All(indexes, index =>
        {
            Assert.True(index.IsUnique);
            Assert.Equal("IdempotencyKeyHash", index.Columns[^1]);
            Assert.Contains(
                $"CREATE UNIQUE INDEX IF NOT EXISTS `{index.Name}`\n    ON `{index.Table}` ({string.Join(", ", index.Columns.Select(column => $"`{column}`"))});",
                script, StringComparison.Ordinal);
        });
        Assert.Contains("'20261013132811_AddCreateIdempotencyKeysAndEpicPlanImports'", script, StringComparison.Ordinal);
    }

    private static Task<KeyedCreateResult> CreateAsync(KanbanDbContext dbContext, string kind) => kind switch
    {
        "epic" => Epics.CreateAsync(dbContext, new Epic { ProjectId = ProjectId, Name = "Epic" }, KeyHash, CancellationToken.None),
        "document" => Epics.CreateDocumentAsync(dbContext, new EpicDocument { EpicId = EpicId, Title = "Doc", Body = "b" }, KeyHash, CancellationToken.None),
        _ => WorkItemWrites.CreateAsync(dbContext, new WorkItem { ProjectId = ProjectId, Title = "Item" }, KeyHash, null, Now, CancellationToken.None)
    };

    // Two items in Ready and one in Backlog, one document and one dependency.
    private static async Task<EpicPlan> PlanAsync() =>
        (await EpicPlans.ReadAsync(
            ProjectId,
            new ImportEpicPlanRequest(
                new ImportEpicPlanEpic("Epic"),
                [new ImportEpicPlanItem("A", "a", Status: "Ready"), new ImportEpicPlanItem("B", "b", BlockedBy: ["A"]), new ImportEpicPlanItem("C", "c", Status: "Ready")],
                "plan-1",
                [new ImportEpicPlanDocument("Doc", "body")]),
            Now)).Plan!;

    private static List<string> Texts(FakeDatabase database) =>
        database.Commands.Select(command => command.Text.ReplaceLineEndings("\n").TrimEnd()).ToList();

    private static Exception Lost(ref bool lost, string message)
    {
        lost = true;
        return ServerError(message, 1062);
    }

    // MySqlException carries the server's error number only through an internal constructor.
    private static Exception ServerError(string message, int number) =>
        (Exception)Activator.CreateInstance(
            typeof(MySqlException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [message, number],
            culture: null)!;

    // keyUsed: whether the look-up of the idempotency key finds an object. Every column ends at order 4.
    private static Func<DbCommand, DataTable?> Answer(Func<bool>? keyUsed = null) => command =>
    {
        var text = command.CommandText;
        if (text.Contains("ROW_COUNT()", StringComparison.Ordinal) && !text.Contains("WorkItemChanges", StringComparison.Ordinal))
        {
            return Table("ROW_COUNT()", 1);
        }

        if (text.Contains("`IdempotencyKeyHash` = ", StringComparison.Ordinal))
        {
            return keyUsed?.Invoke() == true
                ? Table(text.Contains("FROM `EpicPlanImports`", StringComparison.Ordinal) ? "EpicId" : "Id", ExistingId.ToString())
                : null;
        }

        if (text.StartsWith("SELECT EXISTS", StringComparison.Ordinal))
        {
            return Table("e", false);
        }

        return text.StartsWith("SELECT MAX(", StringComparison.Ordinal) ? Table("c", 4) : null;
    };

    private static DataTable Table(string name, object value)
    {
        var table = new DataTable();
        table.Columns.Add(name, value.GetType());
        table.Rows.Add(value);
        return table;
    }
}
