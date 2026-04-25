using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class DbInitializer
{
    public static async Task InitializeAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureProjectArchiveColumnAsync(dbContext, cancellationToken);
        await EnsureEpicsTableAsync(dbContext, cancellationToken);
        await EnsureEpicArchiveColumnAsync(dbContext, cancellationToken);
        await EnsureEpicDocumentsTableAsync(dbContext, cancellationToken);
        await EnsureWorkItemEpicColumnAsync(dbContext, cancellationToken);
        await EnsureWorkItemCommentsTableAsync(dbContext, cancellationToken);
        await EnsureAppUsersTableAsync(dbContext, cancellationToken);
        await EnsurePersonalAccessTokensTableAsync(dbContext, cancellationToken);

        if (await dbContext.Projects.AnyAsync(cancellationToken))
        {
            return;
        }

        var project = new Project
        {
            Name = "Personal Product Roadmap",
            Key = "HOME",
            Description = "Default project for project ideas, backlog grooming, and active work."
        };

        project.Items =
        [
            new WorkItem
            {
                Title = "Shape first milestone",
                Description = "Capture the first delivery slice and success criteria.",
                Type = WorkItemType.Story,
                Status = WorkItemStatus.Ready,
                Priority = WorkItemPriority.High,
                Order = 0,
                Estimate = 3,
                Labels = "planning,milestone"
            },
            new WorkItem
            {
                Title = "Fix rough auth edge case",
                Description = "Document and resolve the intermittent sign-in problem.",
                Type = WorkItemType.Issue,
                Status = WorkItemStatus.InProgress,
                Priority = WorkItemPriority.Critical,
                Order = 0,
                Estimate = 2,
                Labels = "bug,auth"
            },
            new WorkItem
            {
                Title = "Explore export feature",
                Description = "Keep early ideas here until ready for active planning.",
                Type = WorkItemType.Story,
                Status = WorkItemStatus.Backlog,
                Priority = WorkItemPriority.Medium,
                Order = 0,
                Estimate = 5,
                Labels = "ideas,export"
            }
        ];

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureProjectArchiveColumnAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('Projects')";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "IsArchived", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE Projects ADD COLUMN IsArchived INTEGER NOT NULL DEFAULT 0;
                """,
                cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task EnsureEpicsTableAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS Epics (
                Id TEXT NOT NULL CONSTRAINT PK_Epics PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_Epics_Projects_ProjectId FOREIGN KEY (ProjectId) REFERENCES Projects (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_Epics_ProjectId_Name ON Epics (ProjectId, Name);
            """,
            cancellationToken);
    }

    private static async Task EnsureEpicDocumentsTableAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS EpicDocuments (
                Id TEXT NOT NULL CONSTRAINT PK_EpicDocuments PRIMARY KEY,
                EpicId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_EpicDocuments_Epics_EpicId FOREIGN KEY (EpicId) REFERENCES Epics (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_EpicDocuments_EpicId_CreatedAtUtc ON EpicDocuments (EpicId, CreatedAtUtc);
            """,
            cancellationToken);
    }

    private static async Task EnsureEpicArchiveColumnAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('Epics')";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "IsArchived", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE Epics ADD COLUMN IsArchived INTEGER NOT NULL DEFAULT 0;
                """,
                cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task EnsureWorkItemEpicColumnAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('WorkItems')";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "EpicId", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE WorkItems ADD COLUMN EpicId TEXT NULL;
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS IX_WorkItems_EpicId ON WorkItems (EpicId);
                """,
                cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task EnsureWorkItemCommentsTableAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS WorkItemComments (
                Id TEXT NOT NULL CONSTRAINT PK_WorkItemComments PRIMARY KEY,
                WorkItemId TEXT NOT NULL,
                Author TEXT NOT NULL,
                Body TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_WorkItemComments_WorkItems_WorkItemId FOREIGN KEY (WorkItemId) REFERENCES WorkItems (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_WorkItemComments_WorkItemId_CreatedAtUtc ON WorkItemComments (WorkItemId, CreatedAtUtc);
            """,
            cancellationToken);
    }

    private static async Task EnsureAppUsersTableAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS AppUsers (
                Id TEXT NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
                Issuer TEXT NOT NULL,
                Subject TEXT NOT NULL,
                DisplayName TEXT NULL,
                Email TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastSeenAtUtc TEXT NULL
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AppUsers_Issuer_Subject ON AppUsers (Issuer, Subject);
            """,
            cancellationToken);
    }

    private static async Task EnsurePersonalAccessTokensTableAsync(KanbanDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PersonalAccessTokens (
                Id TEXT NOT NULL CONSTRAINT PK_PersonalAccessTokens PRIMARY KEY,
                AppUserId TEXT NOT NULL,
                Name TEXT NOT NULL,
                TokenPrefix TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                EncryptedSecret TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                ExpiresAtUtc TEXT NULL,
                LastUsedAtUtc TEXT NULL,
                RevokedAtUtc TEXT NULL,
                CONSTRAINT FK_PersonalAccessTokens_AppUsers_AppUserId FOREIGN KEY (AppUserId) REFERENCES AppUsers (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_PersonalAccessTokens_AppUserId ON PersonalAccessTokens (AppUserId);
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_PersonalAccessTokens_TokenPrefix ON PersonalAccessTokens (TokenPrefix);
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PersonalAccessTokens_TokenHash ON PersonalAccessTokens (TokenHash);
            """,
            cancellationToken);
    }
}
