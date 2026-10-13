using System.Text.Json;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Services;

public static class KanbanProjectEndpointRouteBuilderExtensions
{
    public const string InactiveTokenMessage =
        "The forwarded personal access token is revoked or expired and cannot be used to create a project.";

    public static IEndpointRouteBuilder MapKanbanProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Archived projects are hidden unless asked for. Each project carries its repository_url and default_branch.
        endpoints.MapGet("/api/projects", async (bool? includeArchived, KanbanDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var includeArchivedProjects = includeArchived ?? false;
            return Results.Ok(await dbContext.Projects
                .AsNoTracking()
                .Where(project => includeArchivedProjects || !project.IsArchived)
                .ToSummaries()
                .ToListAsync(cancellationToken));
        })
        .WithName("ListProjects");

        // Error messages name the arguments as the MCP tools spell them.
        endpoints.MapPost("/api/projects", async (
            CreateProjectRequest request,
            HttpContext httpContext,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var project = new Project { IsArchived = request.IsArchived };
            var refusal = Projects.Apply(project, new ProjectPatch
            {
                Name = request.Name,
                Key = request.Key,
                Description = request.Description,
                // Nothing to clear on a new project: a field that is left out stays unset.
                RepositoryUrl = new(request.RepositoryUrl is not null, request.RepositoryUrl),
                DefaultBranch = new(request.DefaultBranch is not null, request.DefaultBranch)
            });
            if (refusal is not null)
            {
                return InvalidArgument(refusal);
            }

            string? keyHash = null;
            if (request.IdempotencyKey is not null)
            {
                if (!IdempotencyKeys.IsValid(request.IdempotencyKey))
                {
                    return InvalidArgument(IdempotencyKeys.InvalidMessage);
                }

                // A key used with a token belongs to the token's identity; a token that no longer resolves to one
                // must not fall back to the identity that callers without a token share.
                AppUser? identity = null;
                if (KanbanInternalApiExtensions.IsInternalApiRequest(httpContext) &&
                    httpContext.User.FindFirst(KanbanInternalAuth.PersonalAccessTokenIdHeaderName) is not null)
                {
                    identity = await KanbanClaimEndpointRouteBuilderExtensions.ResolveClaimantAsync(
                        httpContext, dbContext, timeProvider.GetUtcNow(), cancellationToken);
                    if (identity is null)
                    {
                        return Error(StatusCodes.Status403Forbidden, KanbanErrorCodes.ScopeDenied, InactiveTokenMessage);
                    }
                }

                keyHash = IdempotencyKeys.Hash(identity?.Id, request.IdempotencyKey);
            }

            var result = await Projects.CreateAsync(dbContext, project, keyHash, cancellationToken);
            if (result.Outcome == ProjectWriteOutcome.Busy)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, KanbanErrorCodes.Busy,
                    $"Other project creates were in progress for {Projects.CreateLockTimeoutSeconds} s. Nothing was created; try again.");
            }

            if (result.Outcome == ProjectWriteOutcome.KeyTaken)
            {
                return KeyTaken(project.Key);
            }

            var created = result.Outcome == ProjectWriteOutcome.Succeeded;
            if (created)
            {
                changeNotifier.Publish();
            }

            // The project as it is now: for a repeated key that is the earlier project, whatever this request asked for.
            var summary = await SummaryAsync(dbContext, result.ProjectId!.Value, cancellationToken);
            if (summary is null)
            {
                return NotFound(result.ProjectId.Value);
            }

            summary = summary with { Created = created };
            return created
                ? Results.Created($"/api/projects/{summary.Id}", summary)
                : Results.Ok(summary);
        })
        .WithName("CreateProject");

        // The full update of the web UI: all four fields are written. The repository URL and default branch are not
        // part of it and keep their values.
        endpoints.MapPut("/api/projects/{projectId:guid}", (
            Guid projectId,
            UpdateProjectRequest request,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            CancellationToken cancellationToken) =>
            UpdateAsync(
                projectId,
                new ProjectPatch { Name = request.Name, Key = request.Key, Description = request.Description, IsArchived = request.IsArchived },
                dbContext, changeNotifier, cancellationToken))
        .WithName("UpdateProject");

        // The partial update: a field that is present is written, null clears it, an absent field is left alone.
        endpoints.MapPatch("/api/projects/{projectId:guid}", (
            Guid projectId,
            JsonElement payload,
            KanbanDbContext dbContext,
            BoardChangeNotifier changeNotifier,
            CancellationToken cancellationToken) =>
            Projects.TryParsePatch(payload, out var patch, out var error)
                ? UpdateAsync(projectId, patch, dbContext, changeNotifier, cancellationToken)
                : Task.FromResult(InvalidArgument(error!)))
        .WithName("PatchProject");

        return endpoints;
    }

    private static async Task<IResult> UpdateAsync(
        Guid projectId,
        ProjectPatch patch,
        KanbanDbContext dbContext,
        BoardChangeNotifier changeNotifier,
        CancellationToken cancellationToken)
    {
        var project = await dbContext.Projects.FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(projectId);
        }

        var refusal = Projects.Apply(project, patch);
        if (refusal is not null)
        {
            return InvalidArgument(refusal);
        }

        var result = await Projects.UpdateAsync(dbContext, project, cancellationToken);
        if (result.Outcome == ProjectWriteOutcome.KeyTaken)
        {
            return KeyTaken(project.Key);
        }

        if (result.Outcome == ProjectWriteOutcome.Succeeded)
        {
            changeNotifier.Publish();
        }

        var summary = await SummaryAsync(dbContext, projectId, cancellationToken);
        return summary is null ? NotFound(projectId) : Results.Ok(summary);
    }

    private static Task<ProjectSummaryDto?> SummaryAsync(KanbanDbContext dbContext, Guid projectId, CancellationToken cancellationToken) =>
        dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .ToSummaries()
            .FirstOrDefaultAsync(cancellationToken);

    private static IResult KeyTaken(string key) =>
        Error(StatusCodes.Status409Conflict, KanbanErrorCodes.Conflict, $"Project key '{key}' already exists. Nothing was changed.");

    private static IResult NotFound(Guid projectId) =>
        Error(StatusCodes.Status404NotFound, KanbanErrorCodes.NotFound, $"Project {projectId} was not found.");

    private static IResult InvalidArgument(string message) =>
        Error(StatusCodes.Status400BadRequest, KanbanErrorCodes.InvalidArgument, message);

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new KanbanErrorDto(code, message), statusCode: statusCode);
}
