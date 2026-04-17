using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var defaultDataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var dataDirectory = builder.Configuration["KANBAN_DATA_DIR"] ?? defaultDataDirectory;
Directory.CreateDirectory(dataDirectory);

var connectionString = builder.Configuration.GetConnectionString("Kanban")
    ?? $"Data Source={Path.Combine(dataDirectory, "kanban.db")}";

builder.Services.AddDbContext<KanbanDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<KanbanDbContext>();
    await DbInitializer.InitializeAsync(dbContext, CancellationToken.None);
}

app.MapOpenApi();
app.MapScalarApiReference("/docs");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/projects", async (bool? includeArchived, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var includeArchivedProjects = includeArchived ?? false;
    var projects = await dbContext.Projects
        .AsNoTracking()
        .Where(project => includeArchivedProjects || !project.IsArchived)
        .Select(project => new
        {
            project.Id,
            project.Name,
            project.Key,
            project.Description,
            project.IsArchived,
            project.CreatedAtUtc,
            TotalItems = project.Items.Count,
            OpenItems = project.Items.Count(item => item.Status != WorkItemStatus.Done)
        })
        .OrderBy(project => project.Name)
        .Select(project => new ProjectSummaryDto(
            project.Id,
            project.Name,
            project.Key,
            project.Description,
            project.IsArchived,
            project.CreatedAtUtc,
            project.TotalItems,
            project.OpenItems))
        .ToListAsync(cancellationToken);

    return Results.Ok(projects);
})
.WithName("ListProjects");

app.MapGet("/api/projects/{projectId:guid}", async (Guid projectId, WorkItemStatus? status, bool? includeArchivedEpics, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var includeArchivedEpicItems = includeArchivedEpics ?? false;
    var project = await dbContext.Projects
        .AsNoTracking()
        .Include(project => project.Items)
            .ThenInclude(item => item.Epic)
        .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);

    return project is null
        ? Results.NotFound()
        : Results.Ok(ToBoardDto(project, status, includeArchivedEpicItems));
})
.WithName("GetProjectBoard");

app.MapGet("/api/projects/{projectId:guid}/epics", async (Guid projectId, bool? includeArchived, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var includeArchivedEpics = includeArchived ?? false;
    var projectExists = await dbContext.Projects
        .AsNoTracking()
        .AnyAsync(project => project.Id == projectId, cancellationToken);

    if (!projectExists)
    {
        return Results.NotFound(new { message = "Project not found." });
    }

    var epics = await dbContext.Epics
        .AsNoTracking()
        .Where(epic => epic.ProjectId == projectId && (includeArchivedEpics || !epic.IsArchived))
        .ToListAsync(cancellationToken);

    return Results.Ok(epics
        .OrderBy(epic => epic.CreatedAtUtc)
        .Select(ToEpicDto)
        .ToList());
})
.WithName("ListProjectEpics");

app.MapGet("/api/epics/{epicId:guid}", async (Guid epicId, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var epic = await dbContext.Epics
        .AsNoTracking()
        .FirstOrDefaultAsync(epic => epic.Id == epicId, cancellationToken);

    return epic is null
        ? Results.NotFound()
        : Results.Ok(ToEpicDto(epic));
})
.WithName("GetEpic");

app.MapGet("/api/epics/{epicId:guid}/documents", async (Guid epicId, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var epicExists = await dbContext.Epics
        .AsNoTracking()
        .AnyAsync(epic => epic.Id == epicId, cancellationToken);

    if (!epicExists)
    {
        return Results.NotFound(new { message = "Epic not found." });
    }

    var documents = await dbContext.EpicDocuments
        .AsNoTracking()
        .Where(document => document.EpicId == epicId)
        .ToListAsync(cancellationToken);

    return Results.Ok(documents
        .OrderBy(document => document.CreatedAtUtc)
        .Select(ToEpicDocumentDto)
        .ToList());
})
.WithName("ListEpicDocuments");

app.MapGet("/api/epic-documents/{documentId:guid}", async (Guid documentId, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var document = await dbContext.EpicDocuments
        .AsNoTracking()
        .FirstOrDefaultAsync(document => document.Id == documentId, cancellationToken);

    return document is null
        ? Results.NotFound()
        : Results.Ok(ToEpicDocumentDto(document));
})
.WithName("GetEpicDocument");

app.MapGet("/api/items", async (Guid? projectId, Guid? epicId, WorkItemType? type, WorkItemStatus? status, bool? includeArchivedEpics, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var includeArchivedEpicItems = includeArchivedEpics ?? false;
    var query = dbContext.WorkItems
        .AsNoTracking()
        .Include(item => item.Epic)
        .AsQueryable();

    if (projectId is not null)
    {
        query = query.Where(item => item.ProjectId == projectId);
    }

    if (epicId is not null)
    {
        query = query.Where(item => item.EpicId == epicId);
    }

    if (type is not null)
    {
        query = query.Where(item => item.Type == type);
    }

    if (status is not null)
    {
        query = query.Where(item => item.Status == status);
    }

    if (!includeArchivedEpicItems)
    {
        query = query.Where(item => item.Epic == null || !item.Epic.IsArchived);
    }

    var items = await query
        .OrderBy(item => item.Status)
        .ThenBy(item => item.Order)
        .ToListAsync(cancellationToken);

    return Results.Ok(items
        .OrderBy(item => item.Status)
        .ThenBy(item => item.Order)
        .ThenBy(item => item.CreatedAtUtc)
        .Select(ToWorkItemDto)
        .ToList());
})
.WithName("ListWorkItems");

app.MapPost("/api/projects", async (CreateProjectRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var normalizedName = request.Name.Trim();
    var normalizedKey = request.Key.Trim().ToUpperInvariant();
    var validationError = ValidateRequiredText(("name", normalizedName), ("key", normalizedKey));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    var keyExists = await dbContext.Projects.AnyAsync(project => project.Key == normalizedKey, cancellationToken);
    if (keyExists)
    {
        return Results.Conflict(new { message = $"Project key '{normalizedKey}' already exists." });
    }

    var project = new Project
    {
        Name = normalizedName,
        Key = normalizedKey,
        Description = request.Description?.Trim(),
        IsArchived = request.IsArchived
    };

    dbContext.Projects.Add(project);
    var saved = await TrySaveProjectAsync(dbContext, normalizedKey, cancellationToken);
    if (!saved)
    {
        return Results.Conflict(new { message = $"Project key '{normalizedKey}' already exists." });
    }

    return Results.Created($"/api/projects/{project.Id}", ToSummaryDto(project));
})
.WithName("CreateProject");

app.MapPut("/api/projects/{projectId:guid}", async (Guid projectId, UpdateProjectRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var project = await dbContext.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    var normalizedName = request.Name.Trim();
    var normalizedKey = request.Key.Trim().ToUpperInvariant();
    var validationError = ValidateRequiredText(("name", normalizedName), ("key", normalizedKey));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    var keyExists = await dbContext.Projects.AnyAsync(
        candidate => candidate.Id != projectId && candidate.Key == normalizedKey,
        cancellationToken);
    if (keyExists)
    {
        return Results.Conflict(new { message = $"Project key '{normalizedKey}' already exists." });
    }

    project.Name = normalizedName;
    project.Key = normalizedKey;
    project.Description = request.Description?.Trim();
    project.IsArchived = request.IsArchived;

    var saved = await TrySaveProjectAsync(dbContext, normalizedKey, cancellationToken);
    if (!saved)
    {
        return Results.Conflict(new { message = $"Project key '{normalizedKey}' already exists." });
    }

    return Results.Ok(ToSummaryDto(project));
})
.WithName("UpdateProject");

app.MapDelete("/api/projects/{projectId:guid}", async (Guid projectId, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var project = await dbContext.Projects.FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    dbContext.Projects.Remove(project);
    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("DeleteProject");

app.MapPost("/api/projects/{projectId:guid}/epics", async (Guid projectId, CreateEpicRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var normalizedName = request.Name.Trim();
    var validationError = ValidateRequiredText(("name", normalizedName));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    var projectExists = await dbContext.Projects.AnyAsync(project => project.Id == projectId, cancellationToken);
    if (!projectExists)
    {
        return Results.NotFound(new { message = "Project not found." });
    }

    var epic = new Epic
    {
        ProjectId = projectId,
        Name = normalizedName,
        Description = request.Description?.Trim(),
        IsArchived = request.IsArchived,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    dbContext.Epics.Add(epic);
    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/epics/{epic.Id}", ToEpicDto(epic));
})
.WithName("CreateEpic");

app.MapPut("/api/epics/{epicId:guid}", async (Guid epicId, UpdateEpicRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var epic = await dbContext.Epics.FirstOrDefaultAsync(candidate => candidate.Id == epicId, cancellationToken);
    if (epic is null)
    {
        return Results.NotFound();
    }

    var normalizedName = request.Name.Trim();
    var validationError = ValidateRequiredText(("name", normalizedName));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    epic.Name = normalizedName;
    epic.Description = request.Description?.Trim();
    epic.IsArchived = request.IsArchived;
    epic.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToEpicDto(epic));
})
.WithName("UpdateEpic");

app.MapDelete("/api/epics/{epicId:guid}", async (Guid epicId, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var epic = await dbContext.Epics.FirstOrDefaultAsync(candidate => candidate.Id == epicId, cancellationToken);
    if (epic is null)
    {
        return Results.NotFound();
    }

    dbContext.Epics.Remove(epic);
    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("DeleteEpic");

app.MapPost("/api/epics/{epicId:guid}/documents", async (Guid epicId, CreateEpicDocumentRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var normalizedTitle = request.Title.Trim();
    var validationError = ValidateRequiredText(("title", normalizedTitle), ("body", request.Body));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    var epicExists = await dbContext.Epics.AnyAsync(epic => epic.Id == epicId, cancellationToken);
    if (!epicExists)
    {
        return Results.NotFound(new { message = "Epic not found." });
    }

    var document = new EpicDocument
    {
        EpicId = epicId,
        Title = normalizedTitle,
        Body = request.Body,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    dbContext.EpicDocuments.Add(document);
    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/epic-documents/{document.Id}", ToEpicDocumentDto(document));
})
.WithName("CreateEpicDocument");

app.MapPut("/api/epic-documents/{documentId:guid}", async (Guid documentId, UpdateEpicDocumentRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var document = await dbContext.EpicDocuments.FirstOrDefaultAsync(candidate => candidate.Id == documentId, cancellationToken);
    if (document is null)
    {
        return Results.NotFound();
    }

    var normalizedTitle = request.Title.Trim();
    var validationError = ValidateRequiredText(("title", normalizedTitle), ("body", request.Body));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    document.Title = normalizedTitle;
    document.Body = request.Body;
    document.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToEpicDocumentDto(document));
})
.WithName("UpdateEpicDocument");

app.MapPost("/api/items", async (CreateWorkItemRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var normalizedTitle = request.Title.Trim();
    var validationError = ValidateRequiredText(("title", normalizedTitle));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    var exists = await dbContext.Projects.AnyAsync(project => project.Id == request.ProjectId, cancellationToken);
    if (!exists)
    {
        return Results.NotFound(new { message = "Project not found." });
    }

    if (request.EpicId is not null)
    {
        var epicExists = await dbContext.Epics.AnyAsync(
            epic => epic.Id == request.EpicId && epic.ProjectId == request.ProjectId && !epic.IsArchived,
            cancellationToken);

        if (!epicExists)
        {
            return Results.BadRequest(new { message = "Epic must belong to the same project as the work item and be active." });
        }
    }

    var order = await GetNextOrderAsync(dbContext, request.ProjectId, request.Status, cancellationToken);
    var item = new WorkItem
    {
        ProjectId = request.ProjectId,
        EpicId = request.EpicId,
        Title = normalizedTitle,
        Description = request.Description?.Trim(),
        Type = request.Type,
        Status = request.Status,
        Priority = request.Priority,
        Order = order,
        Estimate = request.Estimate,
        Labels = NormalizeLabels(request.Labels),
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    dbContext.WorkItems.Add(item);
    await dbContext.SaveChangesAsync(cancellationToken);

    if (item.EpicId is not null)
    {
        await dbContext.Entry(item).Reference(workItem => workItem.Epic).LoadAsync(cancellationToken);
    }

    return Results.Created($"/api/items/{item.Id}", ToWorkItemDto(item));
})
.WithName("CreateWorkItem");

app.MapPut("/api/items/{itemId:guid}", async (Guid itemId, UpdateWorkItemRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var item = await dbContext.WorkItems
        .Include(workItem => workItem.Epic)
        .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
    if (item is null)
    {
        return Results.NotFound();
    }

    var normalizedTitle = request.Title.Trim();
    var validationError = ValidateRequiredText(("title", normalizedTitle));
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    if (request.EpicId is not null)
    {
        var epicExists = await dbContext.Epics.AnyAsync(
            epic => epic.Id == request.EpicId && epic.ProjectId == item.ProjectId && !epic.IsArchived,
            cancellationToken);

        if (!epicExists)
        {
            return Results.BadRequest(new { message = "Epic must belong to the same project as the work item and be active." });
        }
    }

    var statusChanged = item.Status != request.Status;
    item.EpicId = request.EpicId;
    item.Title = normalizedTitle;
    item.Description = request.Description?.Trim();
    item.Type = request.Type;
    item.Status = request.Status;
    item.Priority = request.Priority;
    item.Estimate = request.Estimate;
    item.Labels = NormalizeLabels(request.Labels);
    item.UpdatedAtUtc = DateTimeOffset.UtcNow;

    if (statusChanged)
    {
        item.Order = await GetNextOrderAsync(dbContext, item.ProjectId, item.Status, cancellationToken);
    }

    await dbContext.SaveChangesAsync(cancellationToken);

    if (item.EpicId is null)
    {
        item.Epic = null;
    }
    else
    {
        await dbContext.Entry(item).Reference(workItem => workItem.Epic).LoadAsync(cancellationToken);
    }

    return Results.Ok(ToWorkItemDto(item));
})
.WithName("UpdateWorkItem");

app.MapPost("/api/items/{itemId:guid}/move", async (Guid itemId, MoveWorkItemRequest request, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var item = await dbContext.WorkItems
        .Include(workItem => workItem.Epic)
        .FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
    if (item is null)
    {
        return Results.NotFound();
    }

    item.Status = request.Status;
    item.Order = request.Order;
    item.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await NormalizeColumnOrderingAsync(dbContext, item.ProjectId, request.Status, item.Id, request.Order, cancellationToken);
    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Ok(ToWorkItemDto(item));
})
.WithName("MoveWorkItem");

app.MapDelete("/api/items/{itemId:guid}", async (Guid itemId, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var item = await dbContext.WorkItems.FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
    if (item is null)
    {
        return Results.NotFound();
    }

    dbContext.WorkItems.Remove(item);
    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("DeleteWorkItem");

app.Run();

static ProjectSummaryDto ToSummaryDto(Project project) =>
    new(
        project.Id,
        project.Name,
        project.Key,
        project.Description,
        project.IsArchived,
        project.CreatedAtUtc,
        project.Items.Count,
        project.Items.Count(item => item.Status != WorkItemStatus.Done));

static ProjectBoardDto ToBoardDto(Project project, WorkItemStatus? status = null, bool includeArchivedEpics = false) =>
    new(
        project.Id,
        project.Name,
        project.Key,
        project.Description,
        project.IsArchived,
        project.CreatedAtUtc,
        project.Items
            .Where(item => includeArchivedEpics || item.Epic?.IsArchived != true)
            .Where(item => status is null || item.Status == status)
            .OrderBy(item => item.Status)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.CreatedAtUtc)
            .Select(ToWorkItemDto)
            .ToList());

static EpicDto ToEpicDto(Epic epic) =>
    new(
        epic.Id,
        epic.ProjectId,
        epic.Name,
        epic.Description,
        epic.IsArchived,
        epic.CreatedAtUtc,
        epic.UpdatedAtUtc);

static EpicDocumentDto ToEpicDocumentDto(EpicDocument document) =>
    new(
        document.Id,
        document.EpicId,
        document.Title,
        document.Body,
        document.CreatedAtUtc,
        document.UpdatedAtUtc);

static WorkItemDto ToWorkItemDto(WorkItem item) =>
    new(
        item.Id,
        item.ProjectId,
        item.EpicId,
        item.Epic is null ? null : new EpicReferenceDto(item.Epic.Id, item.Epic.Name),
        item.Title,
        item.Description,
        item.Type,
        item.Status,
        item.Priority,
        item.Order,
        item.Estimate,
        item.Labels,
        item.CreatedAtUtc,
        item.UpdatedAtUtc);

static string? NormalizeLabels(string? labels) =>
    string.IsNullOrWhiteSpace(labels)
        ? null
        : string.Join(',', labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase));

static async Task<int> GetNextOrderAsync(KanbanDbContext dbContext, Guid projectId, WorkItemStatus status, CancellationToken cancellationToken)
{
    var maxOrder = await dbContext.WorkItems
        .Where(item => item.ProjectId == projectId && item.Status == status)
        .Select(item => (int?)item.Order)
        .MaxAsync(cancellationToken);

    return maxOrder.GetValueOrDefault(-1) + 1;
}

static async Task NormalizeColumnOrderingAsync(
    KanbanDbContext dbContext,
    Guid projectId,
    WorkItemStatus status,
    Guid movedItemId,
    int desiredOrder,
    CancellationToken cancellationToken)
{
    var items = await dbContext.WorkItems
        .Where(item => item.ProjectId == projectId && item.Status == status && item.Id != movedItemId)
        .OrderBy(item => item.Order)
        .ToListAsync(cancellationToken);

    // SQLite cannot translate DateTimeOffset ORDER BY, so we apply the tie-breaker in memory.
    items = items
        .OrderBy(item => item.Order)
        .ThenBy(item => item.CreatedAtUtc)
        .ToList();

    desiredOrder = Math.Clamp(desiredOrder, 0, items.Count);
    var index = 0;

    foreach (var item in items)
    {
        if (index == desiredOrder)
        {
            index++;
        }

        item.Order = index++;
        item.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}

static async Task<bool> TrySaveProjectAsync(
    KanbanDbContext dbContext,
    string normalizedKey,
    CancellationToken cancellationToken)
{
    try
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
    catch (DbUpdateException)
    {
        var keyExists = await dbContext.Projects
            .AsNoTracking()
            .AnyAsync(project => project.Key == normalizedKey, cancellationToken);

        if (keyExists)
        {
            return false;
        }

        throw;
    }
}

static string? ValidateRequiredText(params (string FieldName, string? Value)[] fields)
{
    foreach (var (fieldName, value) in fields)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Field '{fieldName}' is required.";
        }
    }

    return null;
}
