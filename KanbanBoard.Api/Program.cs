using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var authOptions = builder.Configuration
    .GetSection(KanbanAuthOptions.SectionName)
    .Get<KanbanAuthOptions>() ?? new KanbanAuthOptions();
var patOptions = builder.Configuration
    .GetSection(PersonalAccessTokenOptions.SectionName)
    .Get<PersonalAccessTokenOptions>() ?? new PersonalAccessTokenOptions();
var internalApiOptions = builder.Configuration
    .GetSection(InternalApiOptions.SectionName)
    .Get<InternalApiOptions>() ?? new InternalApiOptions();
var databaseOptions = builder.Configuration
    .GetSection(DatabaseOptions.SectionName)
    .Get<DatabaseOptions>() ?? new DatabaseOptions();
var connectionString = builder.Configuration.GetConnectionString("Kanban");

var authConfigurationErrors = authOptions.Validate();
var patConfigurationErrors = patOptions.Validate();
var internalApiConfigurationErrors = internalApiOptions.Validate(required: false);
var databaseConfigurationErrors = databaseOptions.Validate(connectionString);
var configurationErrors = authConfigurationErrors
    .Concat(patConfigurationErrors)
    .Concat(internalApiConfigurationErrors)
    .Concat(databaseConfigurationErrors)
    .ToList();
if (configurationErrors.Count > 0)
{
    throw new InvalidOperationException(
        "Authentication is enabled but configuration is invalid: " + string.Join(" ", configurationErrors));
}

builder.Services.AddDbContext<KanbanDbContext>(options => options
    .UseMySQL(
        connectionString!,
        mySqlOptions => mySqlOptions.CommandTimeout(databaseOptions.CommandTimeoutSeconds))
    .AddInterceptors(new MariaDbMigrationLockInterceptor(databaseOptions.MigrationLockTimeoutSeconds)));
builder.Services.Configure<KanbanAuthOptions>(builder.Configuration.GetSection(KanbanAuthOptions.SectionName));
builder.Services.Configure<PersonalAccessTokenOptions>(builder.Configuration.GetSection(PersonalAccessTokenOptions.SectionName));
builder.Services.Configure<InternalApiOptions>(builder.Configuration.GetSection(InternalApiOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.AddKanbanAuthentication(authOptions, builder.Environment);
builder.Services.AddScoped<IPersonalAccessTokenService, PersonalAccessTokenService>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
});
builder.Services.AddOpenApi();
builder.Services.AddSingleton<BoardChangeNotifier>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<KanbanDbContext>();
    if (!await dbContext.Database.CanConnectAsync(CancellationToken.None))
    {
        throw new InvalidOperationException("Unable to connect to the configured MariaDB database.");
    }

    await StartupMigrator.MigrateAsync(
        dbContext,
        databaseOptions,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StartupMigrator)),
        CancellationToken.None);
}

if (ServiceIdentityCommand.IsCommand(args))
{
    // Operator command: runs against the configured database and exits without serving HTTP.
    using var scope = app.Services.CreateScope();
    Environment.ExitCode = await ServiceIdentityCommand.RunAsync(
        args,
        scope.ServiceProvider,
        Console.Out,
        Console.Error,
        CancellationToken.None);
    return;
}

if (authOptions.Enabled)
{
    app.UseForwardedHeaders();
    app.UseAuthentication();
    app.UseKanbanInternalApiAuthentication(internalApiOptions);
    app.UseAuthorization();
    app.UseKanbanAuthenticationGate(authOptions);
    app.UseKanbanAntiforgeryProtection(authOptions);
    app.MapKanbanAuthenticationEndpoints();
}
else
{
    app.UseKanbanInternalApiAuthentication(internalApiOptions);
}

app.UseKanbanBusyRefusal();
app.MapKanbanAntiforgeryEndpoints();
app.MapKanbanSettingsEndpoints();
app.MapKanbanInternalApiEndpoints();
app.MapKanbanProjectEndpoints();
app.MapKanbanEpicPlanEndpoints();
app.MapKanbanClaimEndpoints();
app.MapKanbanDependencyEndpoints();
app.MapKanbanLinkEndpoints();
app.MapKanbanCommentEndpoints();
app.MapKanbanChangeFeedEndpoints();
app.MapKanbanWorkItemReadEndpoints();
app.MapKanbanWorkItemWriteEndpoints();
app.MapOpenApi();
app.MapScalarApiReference("/docs");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/changes", async (HttpContext httpContext, BoardChangeNotifier changeNotifier, CancellationToken cancellationToken) =>
{
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Append("X-Accel-Buffering", "no");
    httpContext.Response.ContentType = "text/event-stream";

    await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
    await httpContext.Response.Body.FlushAsync(cancellationToken);

    using var subscription = changeNotifier.Subscribe();

    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var hasChange = await subscription.Reader.WaitToReadAsync(timeout.Token);
            if (!hasChange)
            {
                break;
            }

            while (subscription.Reader.TryRead(out var change))
            {
                await WriteServerSentChangeAsync(httpContext.Response, change, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await httpContext.Response.WriteAsync(": ping\n\n", cancellationToken);
        }

        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
});

app.MapGet("/api/projects/{projectId:guid}", async (Guid projectId, WorkItemStatus? status, bool? includeArchivedEpics, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    var includeArchivedEpicItems = includeArchivedEpics ?? false;
    var project = await dbContext.Projects
        .AsNoTracking()
        .Include(project => project.Items)
            .ThenInclude(item => item.Epic)
        .Include(project => project.Items)
            .ThenInclude(item => item.Comments)
        .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);

    return project is null
        ? Results.NotFound()
        : Results.Ok(ToBoardDto(
            project,
            await WorkItemDependencies.LoadAsync(dbContext, dbContext.WorkItems.Where(item => item.ProjectId == projectId), cancellationToken),
            await WorkItemLinks.LoadAsync(dbContext, dbContext.WorkItems.Where(item => item.ProjectId == projectId), cancellationToken),
            status,
            includeArchivedEpicItems));
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

app.MapGet("/api/items/search", async (string? query, Guid? projectId, int? limit, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
{
    if (!WorkItemSearch.TryNormalizeTerms(query, out _))
    {
        return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, "Argument 'query' is required.");
    }

    return Results.Ok(await WorkItemSearch.SearchAsync(dbContext, query, projectId, limit, cancellationToken));
})
.WithName("SearchWorkItems");

app.MapDelete("/api/projects/{projectId:guid}", async (Guid projectId, HttpContext httpContext, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    var actor = await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken);
    var now = timeProvider.GetUtcNow();

    // Deleting the project deletes its items, which is a change to each of them, and to every item of another
    // project that one of them blocks. The items and those dependencies are held first, so the entries of the
    // change feed report them as they were deleted. Holding many rows at once can deadlock with other writers.
    // The project's row is held before them, as by every write that changes which items a column holds
    // (WorkItemWrites.HoldColumnsAsync): a move into one of its columns has committed or has not started.
    var deleted = await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await WorkItemWrites.HoldColumnsAsync(dbContext, projectId, cancellationToken);
        await dbContext.WorkItems
            .Where(item => item.ProjectId == projectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), cancellationToken);
        await WorkItemDependencies.HoldBlockedByProjectAsync(dbContext, projectId, cancellationToken);
        var project = await dbContext.Projects.FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
        if (project is null)
        {
            return false;
        }

        var items = await dbContext.WorkItems.AsNoTracking().Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var entries = await WorkItemDependencies.RemovedWithBlockersAsync(
            dbContext.WorkItemDependencies.Where(edge => edge.BlockerItem!.ProjectId == projectId), projectId, actor, now, cancellationToken);
        entries.AddRange(items.Select(item => WorkItemChangeLog.Entry(item.Id, projectId, WorkItemChangeKinds.ItemDeleted, actor, now, WorkItemChangeLog.Snapshot(item), null)));
        dbContext.Projects.Remove(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        await WorkItemChangeLog.CommitAsync(dbContext, transaction, entries, cancellationToken);
        return true;
    });
    if (!deleted)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Project {projectId} was not found.");
    }

    changeNotifier.Publish();
    return Results.NoContent();
})
.WithName("DeleteProject");

// With an idempotency key that the caller already used in the project, nothing is created and that epic is returned
// as it is now, with "created": false.
app.MapPost("/api/projects/{projectId:guid}/epics", async (Guid projectId, CreateEpicRequest request, HttpContext httpContext, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!Epics.TryNew(projectId, request.Name, request.Description, request.IsArchived, out var epic, out var validationError))
    {
        return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, validationError);
    }

    var (keyHash, keyRefusal) = await IdempotencyKeys.HashForRequestAsync(httpContext, dbContext, request.IdempotencyKey, timeProvider.GetUtcNow(), cancellationToken);
    if (keyRefusal is not null)
    {
        return keyRefusal;
    }

    var result = await Epics.CreateAsync(dbContext, epic, keyHash, cancellationToken);
    var stored = result.Outcome == KeyedCreateOutcome.NotFound
        ? null
        : await dbContext.Epics.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == result.Id, cancellationToken);
    if (stored is null)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, "Project not found.");
    }

    if (result.Outcome != KeyedCreateOutcome.Created)
    {
        return Results.Ok(ToEpicDto(stored) with { Created = false });
    }

    changeNotifier.Publish();
    return Results.Created($"/api/epics/{stored.Id}", ToEpicDto(stored) with { Created = true });
})
.WithName("CreateEpic");

app.MapPut("/api/epics/{epicId:guid}", async (Guid epicId, UpdateEpicRequest request, HttpContext httpContext, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    var actor = await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken);
    var normalizedName = request.Name.Trim();
    var validationError = ValidateRequiredText(("name", normalizedName));
    if (!await dbContext.Epics.AnyAsync(candidate => candidate.Id == epicId, cancellationToken))
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Epic {epicId} was not found.");
    }

    if (validationError is not null)
    {
        return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, validationError);
    }

    // Archiving holds the rows of several items at once, which can deadlock with other writers.
    var updated = await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
    {
        // Read before the transaction starts: a read inside it would come before the statement that holds the items.
        var epic = await dbContext.Epics.FirstOrDefaultAsync(candidate => candidate.Id == epicId, cancellationToken);
        if (epic is null)
        {
            return null;
        }

        epic.Name = normalizedName;
        epic.Description = request.Description?.Trim();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        List<WorkItemChange> detached = [];
        if (request.IsArchived && !epic.IsArchived)
        {
            // Archiving takes the unfinished items out of the epic, which is a change to each of them. The project's
            // row first, like every write that takes several items, then the epic's, then the items (see
            // Epics.LockAsync): taking the items first deadlocked with the writes that put an item into the epic.
            await WorkItemWrites.HoldColumnsAsync(dbContext, epic.ProjectId, cancellationToken);
            await Epics.LockAsync(dbContext, epicId, cancellationToken);
            detached = await WorkItemWrites.DetachFromEpicAsync(dbContext, epicId, unfinishedOnly: true, actor, timeProvider.GetUtcNow(), cancellationToken);
        }

        epic.IsArchived = request.IsArchived;
        epic.UpdatedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The epic was deleted, alone or with its project, after it was read: there is nothing to update.
            // Leaving the transaction uncommitted takes back what was done to its items.
            dbContext.ChangeTracker.Clear();
            return null;
        }

        await WorkItemChangeLog.CommitAsync(dbContext, transaction, detached, cancellationToken);
        return epic;
    });
    if (updated is null)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Epic {epicId} was not found.");
    }

    changeNotifier.Publish();
    return Results.Ok(ToEpicDto(updated));
})
.WithName("UpdateEpic");

app.MapDelete("/api/epics/{epicId:guid}", async (Guid epicId, HttpContext httpContext, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    var actor = await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken);
    var epic = await dbContext.Epics.FirstOrDefaultAsync(candidate => candidate.Id == epicId, cancellationToken);
    if (epic is null)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Epic {epicId} was not found.");
    }

    // The database would take the items out of the epic by itself (ON DELETE SET NULL), without stamping them.
    // Taking them out holds the rows of several items at once, which can deadlock with other writers.
    var deleted = await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // The project's row, the epic's, then the items, as when the epic is archived.
        await WorkItemWrites.HoldColumnsAsync(dbContext, epic.ProjectId, cancellationToken);
        await Epics.LockAsync(dbContext, epicId, cancellationToken);
        var detached = await WorkItemWrites.DetachFromEpicAsync(dbContext, epicId, unfinishedOnly: false, actor, timeProvider.GetUtcNow(), cancellationToken);
        dbContext.Epics.Remove(epic);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The epic was deleted, alone or with its project, after it was read: there is nothing to delete.
            // Leaving the transaction uncommitted takes back what was done to its items.
            dbContext.ChangeTracker.Clear();
            return false;
        }

        await WorkItemChangeLog.CommitAsync(dbContext, transaction, detached, cancellationToken);
        return true;
    });
    if (!deleted)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Epic {epicId} was not found.");
    }

    changeNotifier.Publish();
    return Results.NoContent();
})
.WithName("DeleteEpic");

// With an idempotency key that the caller already used on the epic, nothing is created and that document is returned
// as it is now, with "created": false.
app.MapPost("/api/epics/{epicId:guid}/documents", async (Guid epicId, CreateEpicDocumentRequest request, HttpContext httpContext, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!Epics.TryNewDocument(epicId, request.Title, request.Body, out var document, out var validationError))
    {
        return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, validationError);
    }

    var (keyHash, keyRefusal) = await IdempotencyKeys.HashForRequestAsync(httpContext, dbContext, request.IdempotencyKey, timeProvider.GetUtcNow(), cancellationToken);
    if (keyRefusal is not null)
    {
        return keyRefusal;
    }

    var result = await Epics.CreateDocumentAsync(dbContext, document, keyHash, cancellationToken);
    var stored = result.Outcome == KeyedCreateOutcome.NotFound
        ? null
        : await dbContext.EpicDocuments.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == result.Id, cancellationToken);
    if (stored is null)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, "Epic not found.");
    }

    if (result.Outcome != KeyedCreateOutcome.Created)
    {
        return Results.Ok(ToEpicDocumentDto(stored) with { Created = false });
    }

    changeNotifier.Publish();
    return Results.Created($"/api/epic-documents/{stored.Id}", ToEpicDocumentDto(stored) with { Created = true });
})
.WithName("CreateEpicDocument");

app.MapPut("/api/epic-documents/{documentId:guid}", async (Guid documentId, UpdateEpicDocumentRequest request, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, CancellationToken cancellationToken) =>
{
    var document = await dbContext.EpicDocuments.FirstOrDefaultAsync(candidate => candidate.Id == documentId, cancellationToken);
    if (document is null)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Epic document {documentId} was not found.");
    }

    var normalizedTitle = request.Title.Trim();
    var validationError = ValidateRequiredText(("title", normalizedTitle), ("body", request.Body));
    if (validationError is not null)
    {
        return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, validationError);
    }

    document.Title = normalizedTitle;
    document.Body = request.Body;
    document.UpdatedAtUtc = DateTimeOffset.UtcNow;

    try
    {
        await dbContext.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateConcurrencyException)
    {
        // The document was deleted, with its epic, after it was read: the save matched no row, and nothing was written.
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Epic document {documentId} was not found.");
    }

    changeNotifier.Publish();
    return Results.Ok(ToEpicDocumentDto(document));
})
.WithName("UpdateEpicDocument");

// With an idempotency key that the caller already used in the project, nothing is created, nothing is recorded in the
// change feed and that item is returned as it is now, with "created": false.
app.MapPost("/api/items", async (CreateWorkItemRequest request, HttpContext httpContext, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!WorkItemWrites.TryNew(
            request.ProjectId, request.EpicId, request.Title, request.Description, request.Type, request.Status, request.Priority,
            request.Estimate, request.Labels, request.LabelList, out var item, out var validationError))
    {
        return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, validationError);
    }

    var now = timeProvider.GetUtcNow();
    var (keyHash, keyRefusal) = await IdempotencyKeys.HashForRequestAsync(httpContext, dbContext, request.IdempotencyKey, now, cancellationToken);
    if (keyRefusal is not null)
    {
        return keyRefusal;
    }

    var actor = await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken);
    var result = await WorkItemWrites.CreateAsync(dbContext, item, keyHash, actor, now, cancellationToken);
    if (result.Outcome == KeyedCreateOutcome.Invalid)
    {
        return Refusal(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, result.Message!);
    }

    // The item as it is now: for a repeated key that is the earlier item, whatever this request asked for.
    var stored = result.Outcome == KeyedCreateOutcome.NotFound
        ? null
        : await dbContext.WorkItems
            .AsNoTracking()
            .Include(workItem => workItem.Epic)
            .Include(workItem => workItem.Comments)
            .FirstOrDefaultAsync(workItem => workItem.Id == result.Id, cancellationToken);
    if (stored is null)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, "Project not found.");
    }

    var created = result.Outcome == KeyedCreateOutcome.Created;
    var dto = WorkItemDtoMapper.ToDto(
        stored,
        now,
        await WorkItemDependencies.LoadAsync(dbContext, stored.Id, cancellationToken),
        await WorkItemLinks.LoadAsync(dbContext, stored.Id, cancellationToken)) with { Created = created };
    if (!created)
    {
        return Results.Ok(dto);
    }

    changeNotifier.Publish();
    return Results.Created($"/api/items/{stored.Id}", dto);
})
.WithName("CreateWorkItem");

app.MapDelete("/api/items/{itemId:guid}", async (Guid itemId, HttpContext httpContext, KanbanDbContext dbContext, BoardChangeNotifier changeNotifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    var actor = await ChangeActor.ResolveAsync(httpContext, dbContext, cancellationToken);

    // The item is held before it is read, so the entry of the change feed reports the item as it was deleted. So
    // are the dependencies in which it blocks another item: the database deletes them with it, and each is a change
    // to the item it blocked. The delete takes the item's entries out of the indexes while it holds the item's row,
    // and an archive of the epic takes the two in the other order: a deadlock is run again.
    //
    // The delete takes the item out of its column, so it starts by holding the project's row, like a move
    // (WorkItemWrites.HoldColumnsAsync): a move that is renumbering that column has committed or has not started,
    // and does not find a sibling gone. The project is read before the transaction starts: a read inside it would
    // come before the statement that holds the row.
    var projectId = await WorkItemWrites.ProjectOfAsync(dbContext, itemId, cancellationToken);
    var deleted = projectId is not null && await WorkItemChangeLog.RetryDeadlocksAsync(dbContext, async () =>
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await WorkItemWrites.HoldColumnsAsync(dbContext, projectId.Value, cancellationToken);
        await WorkItemChangeLog.LockItemAsync(dbContext, itemId, cancellationToken);
        var blocking = dbContext.WorkItemDependencies.Where(edge => edge.BlockerItemId == itemId);
        await WorkItemDependencies.HoldAsync(blocking, cancellationToken);
        var item = await dbContext.WorkItems.FirstOrDefaultAsync(workItem => workItem.Id == itemId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var entries = await WorkItemDependencies.RemovedWithBlockersAsync(blocking, null, actor, now, cancellationToken);
        entries.Add(WorkItemChangeLog.Entry(itemId, item.ProjectId, WorkItemChangeKinds.ItemDeleted, actor, now, WorkItemChangeLog.Snapshot(item), null));
        dbContext.WorkItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        await WorkItemChangeLog.CommitAsync(dbContext, transaction, entries, cancellationToken);
        return true;
    });
    if (!deleted)
    {
        return Refusal(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Work item {itemId} was not found.");
    }

    changeNotifier.Publish();
    return Results.NoContent();
})
.WithName("DeleteWorkItem");

app.Run();

static ProjectBoardDto ToBoardDto(Project project, WorkItemDependencyLookup dependencies, ILookup<Guid, WorkItemLinkDto> links, WorkItemStatus? status = null, bool includeArchivedEpics = false) =>
    new(
        project.Id,
        project.Name,
        project.Key,
        project.Description,
        project.IsArchived,
        project.CreatedAtUtc,
        project.Items
            .Where(item => includeArchivedEpics || item.Epic?.IsArchived != true)
            .Where(item => status is null ? item.Status != WorkItemStatus.Closed : item.Status == status)
            .OrderBy(item => item.Status)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.CreatedAtUtc)
            .Select(item => WorkItemDtoMapper.ToDto(item, DateTimeOffset.UtcNow, dependencies, links))
            .ToList(),
        project.RepositoryUrl,
        project.DefaultBranch);

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

static IResult Refusal(int statusCode, string code, string message) =>
    Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);

static Task WriteServerSentChangeAsync(HttpResponse response, BoardChangeEvent change, CancellationToken cancellationToken) =>
    response.WriteAsync($"event: changed\ndata: {change.Sequence}\n\n", cancellationToken);

static string? ValidateRequiredText(params (string FieldName, string? Value)[] fields)
{
    foreach (var (fieldName, value) in fields)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Argument '{fieldName}' is required.";
        }
    }

    return null;
}
