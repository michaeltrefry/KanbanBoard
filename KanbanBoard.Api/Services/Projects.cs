using System.Text.Json;
using System.Text.RegularExpressions;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace KanbanBoard.Api.Services;

/// <summary>A field of a partial update: left alone unless <see cref="IsSet"/>, and cleared when set to null.</summary>
public readonly record struct Patched<T>(bool IsSet, T? Value)
{
    public static implicit operator Patched<T>(T? value) => new(true, value);
}

/// <summary>The fields of a project a caller may write. A field that is not set keeps its value.</summary>
public sealed record ProjectPatch
{
    public Patched<string> Name { get; init; }
    public Patched<string> Key { get; init; }
    public Patched<string> Description { get; init; }
    public Patched<string> RepositoryUrl { get; init; }
    public Patched<string> DefaultBranch { get; init; }
    public bool? IsArchived { get; init; }
}

public enum ProjectWriteOutcome
{
    Succeeded,
    /// <summary>Nothing was written: the update changed no field, or the idempotency key already belongs to a project.</summary>
    Unchanged,
    /// <summary>Another project has the key.</summary>
    KeyTaken,
    /// <summary>Other keyed creates held the lock for the whole wait; nothing was read or written.</summary>
    Busy
}

/// <param name="ProjectId">The project that was written, or the one the idempotency key already belonged to.</param>
public sealed record ProjectWriteResult(ProjectWriteOutcome Outcome, Guid? ProjectId = null);

/// <summary>
/// Project writes and the rules for a project's fields. Everything here runs on SQLite in the tests and on MariaDB
/// through MySql.EntityFrameworkCore in production.
/// </summary>
public static partial class Projects
{
    public const string CreateLockName = "kanban_project_creates";
    public const int CreateLockTimeoutSeconds = 10;

    // ER_DUP_ENTRY, and the unique index on Projects.Key as the server names it in that error.
    private const int DuplicateEntryError = 1062;
    private const string KeyIndexName = "IX_Projects_Key";

    // user@host:path, the form git prints for an SSH remote. No password can be written in it, and the path does
    // not start with '-'.
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*:(?!-)[^@\s]+$")]
    private static partial Regex ScpLikeUrl();

    /// <summary>
    /// Accepts a repository URL of at most <see cref="ProjectLimits.MaxRepositoryUrlLength"/> characters without
    /// whitespace or control characters, in one of three forms: an http or https URL as a work item link takes it
    /// (<see cref="WorkItemLinks.TryNormalizeUrl"/>: absolute, no user name or password), an <c>ssh://</c> URL with
    /// a host and a path and at most a user name, or <c>user@host:path</c>. A URL that carries a password is refused
    /// in every form: the URL is returned to every read token. So is an SSH URL whose user name holds a <c>%</c>
    /// (git decodes an <c>ssh://</c> URL before use, so <c>%3A</c> would hide a password) and one whose path starts
    /// with <c>-</c>, written out or as <c>%2D</c> (which <see cref="Uri"/> decodes).
    /// </summary>
    public static bool TryNormalizeRepositoryUrl(string? value, out string url)
    {
        url = value?.Trim() ?? string.Empty;
        if (url.Length is 0 or > ProjectLimits.MaxRepositoryUrlLength ||
            url.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        if (WorkItemLinks.TryNormalizeUrl(url, out _))
        {
            return true;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == "ssh")
        {
            // A host, user name or path that starts with '-' would be read by ssh or git as an option.
            return parsed.Host.Length > 0 &&
                !parsed.Host.StartsWith('-') &&
                !parsed.UserInfo.StartsWith('-') &&
                !parsed.UserInfo.Contains(':') &&
                !parsed.UserInfo.Contains('%') &&
                parsed.AbsolutePath.Length > 1 &&
                !parsed.AbsolutePath.StartsWith("/-", StringComparison.Ordinal);
        }

        return ScpLikeUrl().IsMatch(url);
    }

    /// <summary>
    /// Accepts a branch name of at most <see cref="ProjectLimits.MaxDefaultBranchLength"/> characters that git
    /// accepts (git check-ref-format): no whitespace or control character, none of <c>~ ^ : ? * [ \</c>, no
    /// <c>..</c>, <c>@{</c> or <c>//</c>, not starting or ending with <c>/</c>, not ending with <c>.</c>, no part
    /// between slashes starting with <c>.</c> or ending with <c>.lock</c>, and not <c>@</c> alone. A name that
    /// starts with <c>-</c> is refused as well: a command line would read it as an option.
    /// </summary>
    public static bool TryNormalizeDefaultBranch(string? value, out string branch)
    {
        branch = value?.Trim() ?? string.Empty;
        return branch.Length is > 0 and <= ProjectLimits.MaxDefaultBranchLength &&
            !branch.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || "~^:?*[\\".Contains(character)) &&
            !branch.StartsWith('-') &&
            !branch.StartsWith('/') &&
            !branch.EndsWith('/') &&
            !branch.EndsWith('.') &&
            branch != "@" &&
            !branch.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)) &&
            !branch.Contains("..", StringComparison.Ordinal) &&
            !branch.Contains("@{", StringComparison.Ordinal) &&
            !branch.Contains("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the body of a partial update: a JSON object in which a field that is present is written, a field that
    /// is null is cleared, and a field that is absent is left alone. A field the project does not have is refused,
    /// so a misspelt one is not silently ignored.
    /// </summary>
    public static bool TryParsePatch(JsonElement payload, out ProjectPatch patch, out string? error)
    {
        patch = new ProjectPatch();
        error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The request body must be a JSON object.";
            return false;
        }

        foreach (var property in payload.EnumerateObject())
        {
            var value = property.Value;
            if (property.Name == "isArchived")
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "Argument 'is_archived' must be true or false.";
                    return false;
                }

                patch = patch with { IsArchived = value.GetBoolean() };
                continue;
            }

            if (property.Name is not ("name" or "key" or "description" or "repositoryUrl" or "defaultBranch"))
            {
                error = $"A project has no field '{property.Name}'. The fields are name, key, description, repositoryUrl, defaultBranch and isArchived.";
                return false;
            }

            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                error = $"Field '{property.Name}' must be a string or null.";
                return false;
            }

            Patched<string> text = value.GetString();
            patch = property.Name switch
            {
                "name" => patch with { Name = text },
                "key" => patch with { Key = text },
                "description" => patch with { Description = text },
                "repositoryUrl" => patch with { RepositoryUrl = text },
                _ => patch with { DefaultBranch = text }
            };
        }

        return true;
    }

    /// <summary>
    /// Writes the set fields of <paramref name="patch"/> to <paramref name="project"/>, or writes none of them and
    /// returns why the patch is refused. Messages name the arguments as the MCP tools spell them.
    /// </summary>
    public static string? Apply(Project project, ProjectPatch patch)
    {
        var name = patch.Name.Value?.Trim();
        if (patch.Name.IsSet && (string.IsNullOrEmpty(name) || name.Length > ProjectLimits.MaxNameLength))
        {
            return $"Argument 'name' is required and must be at most {ProjectLimits.MaxNameLength} characters.";
        }

        var key = patch.Key.Value?.Trim().ToUpperInvariant();
        if (patch.Key.IsSet && (string.IsNullOrEmpty(key) || key.Length > ProjectLimits.MaxKeyLength))
        {
            return $"Argument 'key' is required and must be at most {ProjectLimits.MaxKeyLength} characters.";
        }

        string? repositoryUrl = null;
        if (patch.RepositoryUrl is { IsSet: true, Value: not null } && !TryNormalizeRepositoryUrl(patch.RepositoryUrl.Value, out repositoryUrl))
        {
            return $"Argument 'repository_url' must be an http, https or ssh repository URL (https://host/path, ssh://user@host/path or user@host:path) " +
                $"of at most {ProjectLimits.MaxRepositoryUrlLength} characters, without whitespace and without a password or, for http and https, a user name. " +
                "In an SSH URL the user name holds no '%' and the path does not start with '-'.";
        }

        string? defaultBranch = null;
        if (patch.DefaultBranch is { IsSet: true, Value: not null } && !TryNormalizeDefaultBranch(patch.DefaultBranch.Value, out defaultBranch))
        {
            return $"Argument 'default_branch' must be a branch name of at most {ProjectLimits.MaxDefaultBranchLength} characters that git accepts: " +
                "no whitespace, none of ~ ^ : ? * [ \\, no '..', '@{' or '//', not starting with '-' or '/', not ending with '/' or '.', " +
                "no part between slashes starting with '.' or ending with '.lock', and not '@' alone.";
        }

        if (patch.Name.IsSet)
        {
            project.Name = name!;
        }

        if (patch.Key.IsSet)
        {
            project.Key = key!;
        }

        if (patch.Description.IsSet)
        {
            project.Description = patch.Description.Value?.Trim();
        }

        if (patch.RepositoryUrl.IsSet)
        {
            project.RepositoryUrl = repositoryUrl;
        }

        if (patch.DefaultBranch.IsSet)
        {
            project.DefaultBranch = defaultBranch;
        }

        project.IsArchived = patch.IsArchived ?? project.IsArchived;
        return null;
    }

    /// <summary>
    /// Holds the project's row until the transaction ends and changes nothing; returns 0 when there is no such
    /// project. The keyed creates of the project's epics, every create of an item, the imports into it, every move
    /// and update of an item that names a status, every delete of an item and the delete of the project start with
    /// it, so they run one at a time (see
    /// <see cref="IdempotencyKeys.CreateOnceAsync"/> and <see cref="WorkItemWrites.MoveAsync"/>).
    /// </summary>
    public static Task<int> LockAsync(KanbanDbContext dbContext, Guid projectId, CancellationToken cancellationToken) =>
        dbContext.Projects
            .Where(project => project.Id == projectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.IsArchived, project => project.IsArchived), cancellationToken);

    /// <summary>
    /// Stores a new project. With an <paramref name="idempotencyKeyHash"/> that already belongs to a project,
    /// nothing is written and that project is reported, whatever its fields.
    /// </summary>
    public static Task<ProjectWriteResult> CreateAsync(
        KanbanDbContext dbContext,
        Project project,
        string? idempotencyKeyHash,
        CancellationToken cancellationToken)
    {
        if (idempotencyKeyHash is null)
        {
            return InsertAsync(dbContext, project, cancellationToken);
        }

        // A project has no parent row whose lock could make two creates with one key wait for each other, so the
        // look-up of the key and the insert run one caller at a time: the second of two creates with one key then
        // finds the first one's project instead of failing on the unique index.
        return MariaDbNamedLock.RunAsync(dbContext, CreateLockName, CreateLockTimeoutSeconds, new ProjectWriteResult(ProjectWriteOutcome.Busy), async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var earlier = await dbContext.Projects
                .AsNoTracking()
                .Where(candidate => candidate.IdempotencyKeyHash == idempotencyKeyHash)
                .Select(candidate => new { candidate.Id })
                .FirstOrDefaultAsync(cancellationToken);
            if (earlier is not null)
            {
                return new ProjectWriteResult(ProjectWriteOutcome.Unchanged, earlier.Id);
            }

            project.IdempotencyKeyHash = idempotencyKeyHash;
            var result = await InsertAsync(dbContext, project, cancellationToken);
            if (result.Outcome == ProjectWriteOutcome.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }, cancellationToken);
    }

    private static async Task<ProjectWriteResult> InsertAsync(KanbanDbContext dbContext, Project project, CancellationToken cancellationToken)
    {
        if (await dbContext.Projects.AnyAsync(candidate => candidate.Key == project.Key, cancellationToken))
        {
            return new(ProjectWriteOutcome.KeyTaken);
        }

        dbContext.Projects.Add(project);
        return await SaveAsync(dbContext, project, cancellationToken);
    }

    /// <summary>Saves the changes <see cref="Apply"/> made to a tracked project.</summary>
    public static async Task<ProjectWriteResult> UpdateAsync(KanbanDbContext dbContext, Project project, CancellationToken cancellationToken)
    {
        var entry = dbContext.Entry(project);
        if (entry.State == EntityState.Unchanged)
        {
            return new(ProjectWriteOutcome.Unchanged, project.Id);
        }

        if (entry.Property(candidate => candidate.Key).IsModified &&
            await dbContext.Projects.AnyAsync(candidate => candidate.Id != project.Id && candidate.Key == project.Key, cancellationToken))
        {
            return new(ProjectWriteOutcome.KeyTaken);
        }

        return await SaveAsync(dbContext, project, cancellationToken);
    }

    private static async Task<ProjectWriteResult> SaveAsync(KanbanDbContext dbContext, Project project, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(ProjectWriteOutcome.Succeeded, project.Id);
        }
        catch (DbUpdateException exception)
        {
            // Another writer took the key between the check and the save. On MariaDB the server says so, and it
            // is taken at its word: a keyed create runs in a transaction whose snapshot (REPEATABLE READ) was
            // taken before that writer committed, so the check below would not see the row.
            dbContext.ChangeTracker.Clear();
            if (exception.InnerException is MySqlException { Number: DuplicateEntryError } duplicate &&
                duplicate.Message.Contains(KeyIndexName, StringComparison.Ordinal))
            {
                return new(ProjectWriteOutcome.KeyTaken);
            }

            if (await dbContext.Projects.AsNoTracking().AnyAsync(candidate => candidate.Id != project.Id && candidate.Key == project.Key, cancellationToken))
            {
                return new(ProjectWriteOutcome.KeyTaken);
            }

            throw;
        }
    }

    /// <summary>The summary of each of <paramref name="projects"/> with its item counts, ordered by name.</summary>
    public static IQueryable<ProjectSummaryDto> ToSummaries(this IQueryable<Project> projects) =>
        projects
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.Key,
                project.Description,
                project.IsArchived,
                project.CreatedAtUtc,
                TotalItems = project.Items.Count,
                OpenItems = project.Items.Count(item => item.Status != WorkItemStatus.Done && item.Status != WorkItemStatus.Closed),
                project.RepositoryUrl,
                project.DefaultBranch
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
                project.OpenItems,
                project.RepositoryUrl,
                project.DefaultBranch));
}
