using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static KanbanBoard.Tests.WorkItemClaimApiTests;

namespace KanbanBoard.Tests;

// Runs the project endpoints against SQLite on a file, like WorkItemCommentApiTests: real SQL, a real unique index,
// and one connection per request so concurrent creates genuinely race.
public sealed class ProjectApiTests
{
    private const string Repository = "https://github.com/acme/app.git";

    [Fact]
    public async Task List_returns_repository_url_and_default_branch_for_every_project_null_when_unset()
    {
        using var fixture = await ApiFixture.CreateAsync();
        await SeedAsync(fixture, new Project { Name = "B plain", Key = "PLAIN" });
        await SeedAsync(fixture, new Project { Name = "A target", Key = "TARGET", RepositoryUrl = Repository, DefaultBranch = "main" });
        await SeedAsync(fixture, new Project { Name = "C archived", Key = "OLD", IsArchived = true, RepositoryUrl = "git@github.com:acme/old.git" });

        // The call as it was made before the fields existed.
        var raw = await fixture.Client.GetStringAsync("/api/projects");
        var withArchived = await fixture.Client.GetFromJsonAsync<List<ProjectSummaryDto>>("/api/projects?includeArchived=true");

        using var json = JsonDocument.Parse(raw);
        var listed = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(["A target", "B plain"], listed.Select(project => project.GetProperty("name").GetString()).ToArray());
        Assert.All(listed, project => Assert.Equal(
            ["createdAtUtc", "default_branch", "description", "id", "isArchived", "key", "name", "openItems", "repository_url", "totalItems"],
            project.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()));
        Assert.Equal((Repository, "main"), (listed[0].GetProperty("repository_url").GetString(), listed[0].GetProperty("default_branch").GetString()));
        Assert.Equal(
            (JsonValueKind.Null, JsonValueKind.Null),
            (listed[1].GetProperty("repository_url").ValueKind, listed[1].GetProperty("default_branch").ValueKind));
        Assert.Equal(["A target", "B plain", "C archived"], withArchived!.Select(project => project.Name).ToArray());
        Assert.Equal(("git@github.com:acme/old.git", null), (withArchived[2].RepositoryUrl, withArchived[2].DefaultBranch));
    }

    [Fact]
    public async Task Create_stores_the_repository_url_and_default_branch_and_says_it_created_the_project()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");

        using var response = await fixture.PostAsync("/api/projects", factory, new
        {
            name = " App ", key = " app ", description = " d ", repositoryUrl = $" {Repository} ", defaultBranch = " release/1.x ", idempotencyKey = "plan-1"
        });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, raw);
        var created = JsonSerializer.Deserialize<ProjectSummaryDto>(raw, JsonSerializerOptions.Web)!;
        Assert.Equal(("App", "APP", "d", Repository, "release/1.x", true), (created.Name, created.Key, created.Description, created.RepositoryUrl, created.DefaultBranch, created.Created));
        var stored = Assert.Single(await ProjectsAsync(fixture));
        Assert.Equal((created.Id, Repository, "release/1.x"), (stored.Id, stored.RepositoryUrl, stored.DefaultBranch));
        // The key itself is not stored.
        Assert.Equal(IdempotencyKeys.Hash(factory.User.Id, "plan-1"), stored.IdempotencyKeyHash);
    }

    [Fact]
    public async Task Create_with_the_fields_it_always_had_still_works_and_leaves_the_new_ones_null()
    {
        using var fixture = await ApiFixture.CreateAsync();

        using var response = await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "App", key = "app", description = (string?)null, isArchived = false });
        var listed = await fixture.Client.GetFromJsonAsync<List<ProjectSummaryDto>>("/api/projects");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ProjectSummaryDto>())!;
        Assert.Equal(("App", "APP", null, null, true), (created.Name, created.Key, created.RepositoryUrl, created.DefaultBranch, created.Created));
        // "created" belongs to the answer of a create only.
        Assert.Null(Assert.Single(listed!).Created);
        Assert.Null(Assert.Single(await ProjectsAsync(fixture)).IdempotencyKeyHash);
    }

    [Fact]
    public async Task A_repeated_idempotency_key_returns_the_same_project_and_creates_nothing()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        using var changes = fixture.SubscribeToBoardChanges();
        int Published()
        {
            var count = 0;
            while (changes.Reader.TryRead(out _))
            {
                count++;
            }

            return count;
        }

        var first = await ReadAsync(await fixture.PostAsync("/api/projects", factory, new { name = "App", key = "APP", repositoryUrl = Repository, idempotencyKey = "plan-1" }));
        Assert.Equal(1, Published());
        await fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            dbContext.WorkItems.Add(new WorkItem { ProjectId = first.Id, Title = "open" });
            dbContext.WorkItems.Add(new WorkItem { ProjectId = first.Id, Title = "done", Status = WorkItemStatus.Done });
            return await dbContext.SaveChangesAsync();
        });
        using var repeatResponse = await fixture.PostAsync("/api/projects", factory, new { name = "App", key = "APP", repositoryUrl = Repository, idempotencyKey = "plan-1" });
        // The key names the project: a repeat with other content still reports the first one, unchanged.
        using var otherResponse = await fixture.PostAsync("/api/projects", factory, new { name = "Other", key = "OTHER", repositoryUrl = "https://example.test/other.git", idempotencyKey = "plan-1" });

        Assert.Equal(HttpStatusCode.OK, repeatResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
        foreach (var repeat in new[] { await ReadAsync(repeatResponse), await ReadAsync(otherResponse) })
        {
            Assert.Equal((first.Id, false, "App", "APP", Repository), (repeat.Id, repeat.Created, repeat.Name, repeat.Key, repeat.RepositoryUrl));
            // The project as it is now, not as it was created.
            Assert.Equal((2, 1), (repeat.TotalItems, repeat.OpenItems));
        }

        Assert.True(first.Created);
        Assert.Equal(0, Published());
        Assert.Equal("App", Assert.Single(await ProjectsAsync(fixture)).Name);
    }

    [Fact]
    public async Task An_idempotency_key_belongs_to_one_identity_and_is_compared_exactly()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var other = await fixture.CreateCallerAsync("Other");

        var ids = new List<Guid>
        {
            (await ReadAsync(await fixture.PostAsync("/api/projects", factory, new { name = "1", key = "P1", idempotencyKey = "key" }))).Id,
            (await ReadAsync(await fixture.PostAsync("/api/projects", other, new { name = "2", key = "P2", idempotencyKey = "key" }))).Id,
            (await ReadAsync(await fixture.PostAsync("/api/projects", factory, new { name = "3", key = "P3", idempotencyKey = "KEY" }))).Id,
            (await ReadAsync(await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "4", key = "P4", idempotencyKey = "key" }))).Id,
            // Without a key every call is a new project.
            (await ReadAsync(await fixture.PostAsync("/api/projects", factory, new { name = "5", key = "P5" }))).Id
        };
        // Callers without a token share one identity.
        var repeatedWithoutToken = await ReadAsync(await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "6", key = "P6", idempotencyKey = "key" }));

        Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal((ids[3], false), (repeatedWithoutToken.Id, repeatedWithoutToken.Created));
        Assert.Equal(5, (await ProjectsAsync(fixture)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData("tab\there")]
    [InlineData("café")]
    public async Task A_malformed_idempotency_key_is_refused_invalid_argument_and_creates_nothing(string key)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");

        using var response = await fixture.PostAsync("/api/projects", factory, new { name = "App", key = "APP", idempotencyKey = key });
        using var tooLong = await fixture.PostAsync("/api/projects", factory, new { name = "App", key = "APP", idempotencyKey = new string('k', 129) });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(await ProjectsAsync(fixture));
    }

    [Fact]
    public async Task Concurrent_creates_with_one_idempotency_key_store_exactly_one_project_and_all_report_it()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");

        for (var round = 0; round < 6; round++)
        {
            using var start = new SemaphoreSlim(0);
            var attempts = Enumerable.Range(0, 6)
                .Select(attempt => Task.Run(async () =>
                {
                    await start.WaitAsync();
                    return await fixture.PostAsync("/api/projects", factory, new { name = $"Attempt {attempt}", key = $"R{round}", idempotencyKey = $"round-{round}" });
                }))
                .ToList();
            start.Release(attempts.Count);
            var responses = await Task.WhenAll(attempts);

            var results = new List<ProjectSummaryDto>();
            foreach (var response in responses)
            {
                results.Add(await ReadAsync(response));
            }

            var stored = Assert.Single(await ProjectsAsync(fixture), project => project.Key == $"R{round}");
            Assert.All(results, result => Assert.Equal(stored.Id, result.Id));
            Assert.Equal(1, results.Count(result => result.Created == true));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        }
    }

    [Fact]
    public async Task Create_with_a_key_and_a_revoked_token_is_refused_scope_denied_and_never_falls_back_to_the_shared_identity()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        await fixture.WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().PersonalAccessTokens
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAtUtc, fixture.Clock.Now)));

        using var response = await fixture.PostAsync("/api/projects", caller, new { name = "App", key = "APP", idempotencyKey = "key" });

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(await ProjectsAsync(fixture));
    }

    [Fact]
    public async Task Create_with_a_key_another_project_has_is_refused_conflict_and_a_malformed_name_or_key_invalid_argument()
    {
        using var fixture = await ApiFixture.CreateAsync();
        await SeedAsync(fixture, new Project { Name = "App", Key = "APP" });

        using var duplicate = await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "Again", key = "app", idempotencyKey = "k" });
        using var noName = await fixture.Client.PostAsJsonAsync("/api/projects", new { name = " ", key = "NEW" });
        using var missingKey = await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "New" });
        using var longName = await fixture.Client.PostAsJsonAsync("/api/projects", new { name = new string('n', 161), key = "NEW" });
        using var longKey = await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "New", key = new string('K', 25) });

        await AssertErrorAsync(duplicate, HttpStatusCode.Conflict, KanbanErrorCodes.Conflict);
        await AssertErrorAsync(noName, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(missingKey, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(longName, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(longKey, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Single(await ProjectsAsync(fixture));
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git")]
    [InlineData("http://git.internal:8080/acme/app")]
    [InlineData("ssh://git@github.com/acme/app.git")]
    [InlineData("ssh://git.internal:2222/srv/git/app.git")]
    [InlineData("git@github.com:acme/app.git")]
    [InlineData("deploy.bot@git.internal:/srv/git/app.git")]
    [InlineData("ssh://git@github.com/acme/-app.git")]
    [InlineData("git@github.com:acme/-app.git")]
    public void A_repository_url_is_http_https_or_ssh(string url)
    {
        Assert.True(Projects.TryNormalizeRepositoryUrl($"  {url} ", out var normalized));
        Assert.Equal(url, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("github.com/acme/app")]
    [InlineData("/srv/git/app.git")]
    [InlineData("file:///srv/git/app.git")]
    [InlineData("git://github.com/acme/app.git")]
    [InlineData("ext::sh -c evil")]
    [InlineData("https://github.com/acme/my app.git")]
    [InlineData("https://github.com/acme/\napp.git")]
    [InlineData("https://user:secret@github.com/acme/app.git")]
    [InlineData("https://x-access-token@github.com/acme/app.git")]
    [InlineData("ssh://git:secret@github.com/acme/app.git")]
    [InlineData("ssh://git@github.com")]
    [InlineData("ssh://-oProxyCommand=evil/acme/app.git")]
    [InlineData("git:secret@github.com:acme/app.git")]
    [InlineData("-oProxyCommand=evil@github.com:acme/app.git")]
    [InlineData("git@github.com:")]
    [InlineData("git@github.com:acme/secret@app.git")]
    [InlineData("ssh://user%3Apw@github.com/acme/app.git")]
    [InlineData("ssh://user%3apw@github.com/acme/app.git")]
    [InlineData("ssh://git@github.com/-oProxyCommand=evil")]
    [InlineData("ssh://git@github.com/%2DoProxyCommand=evil")]
    [InlineData("git@github.com:-oProxyCommand=evil")]
    public void A_repository_url_that_is_not_one_or_carries_credentials_is_refused(string url) =>
        Assert.False(Projects.TryNormalizeRepositoryUrl(url, out _));

    [Fact]
    public void A_repository_url_is_at_most_2048_characters()
    {
        var longest = "https://example.test/" + new string('a', 2048 - "https://example.test/".Length);

        Assert.True(Projects.TryNormalizeRepositoryUrl(longest, out _));
        Assert.False(Projects.TryNormalizeRepositoryUrl(longest + "a", out _));
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/1.x", true)]
    [InlineData("feature/sc-25864_target-repo", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("tab\there", false)]
    [InlineData("-b", false)]
    [InlineData("--upload-pack=evil", false)]
    [InlineData("a..b", false)]
    [InlineData("a~1", false)]
    [InlineData("a^", false)]
    [InlineData("a:b", false)]
    [InlineData("a?", false)]
    [InlineData("a*", false)]
    [InlineData("a[b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a@{b", false)]
    [InlineData("/main", false)]
    [InlineData("main/", false)]
    [InlineData("a//b", false)]
    [InlineData("main.", false)]
    [InlineData("main.lock", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/.b", false)]
    [InlineData("a.lock/b", false)]
    [InlineData("@", false)]
    [InlineData("a/@", true)]
    [InlineData("a.b/c-d", true)]
    public void A_default_branch_is_a_name_git_accepts(string branch, bool valid) =>
        Assert.Equal(valid, Projects.TryNormalizeDefaultBranch(branch, out _));

    [Fact]
    public void A_default_branch_is_at_most_255_characters_and_surrounding_whitespace_is_dropped()
    {
        Assert.True(Projects.TryNormalizeDefaultBranch(" main ", out var normalized));
        Assert.Equal("main", normalized);
        Assert.True(Projects.TryNormalizeDefaultBranch(new string('b', 255), out _));
        Assert.False(Projects.TryNormalizeDefaultBranch(new string('b', 256), out _));
    }

    [Theory]
    [InlineData("repositoryUrl", "https://user:secret@github.com/acme/app.git")]
    [InlineData("repositoryUrl", "not a url")]
    [InlineData("repositoryUrl", "")]
    [InlineData("defaultBranch", "two words")]
    [InlineData("defaultBranch", " ")]
    public async Task A_malformed_repository_url_or_default_branch_is_refused_invalid_argument_by_create_and_by_update(string field, string value)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP", RepositoryUrl = Repository, DefaultBranch = "main" });

        using var created = await fixture.Client.PostAsJsonAsync("/api/projects", new Dictionary<string, object?> { ["name"] = "New", ["key"] = "NEW", [field] = value });
        using var updated = await PatchAsync(fixture, project, new Dictionary<string, object?> { ["name"] = "Renamed", [field] = value });

        await AssertErrorAsync(created, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(updated, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        var stored = Assert.Single(await ProjectsAsync(fixture));
        // Nothing of a refused update is applied, not even its valid fields.
        Assert.Equal(("App", Repository, "main"), (stored.Name, stored.RepositoryUrl, stored.DefaultBranch));
    }

    [Fact]
    public async Task Update_changes_only_the_fields_passed()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP", Description = "about", RepositoryUrl = Repository, DefaultBranch = "main" });
        await SeedAsync(fixture, new Project { Name = "Bystander", Key = "BY", RepositoryUrl = Repository, DefaultBranch = "main" });

        var branch = await ReadAsync(await PatchAsync(fixture, project, new { defaultBranch = "develop" }));
        var renamed = await ReadAsync(await PatchAsync(fixture, project, new { name = " Renamed " }));
        var rekeyed = await ReadAsync(await PatchAsync(fixture, project, new { key = "app2" }));
        var described = await ReadAsync(await PatchAsync(fixture, project, new { description = "new text" }));
        var retargeted = await ReadAsync(await PatchAsync(fixture, project, new { repositoryUrl = "git@github.com:acme/app.git" }));

        Assert.Equal(("App", "APP", "about", Repository, "develop", false), Fields(branch));
        Assert.Equal(("Renamed", "APP", "about", Repository, "develop", false), Fields(renamed));
        Assert.Equal(("Renamed", "APP2", "about", Repository, "develop", false), Fields(rekeyed));
        Assert.Equal(("Renamed", "APP2", "new text", Repository, "develop", false), Fields(described));
        Assert.Equal(("Renamed", "APP2", "new text", "git@github.com:acme/app.git", "develop", false), Fields(retargeted));
        Assert.Null(retargeted.Created);
        var stored = await ProjectsAsync(fixture);
        Assert.Equal(("Bystander", "BY", Repository, "main"), stored.Where(candidate => candidate.Id != project).Select(other => (other.Name, other.Key, other.RepositoryUrl, other.DefaultBranch)).Single());
        Assert.Equal(("Renamed", "APP2", "new text", "git@github.com:acme/app.git", "develop", false), stored.Where(candidate => candidate.Id == project).Select(Stored).Single());
    }

    [Fact]
    public async Task Update_clears_repository_url_default_branch_and_description_with_an_explicit_null()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP", Description = "about", RepositoryUrl = Repository, DefaultBranch = "main" });

        var noBranch = await ReadAsync(await PatchAsync(fixture, project, new { defaultBranch = (string?)null }));
        using var raw = await PatchAsync(fixture, project, new { repositoryUrl = (string?)null, description = (string?)null });

        Assert.Equal(("App", "APP", "about", Repository, null, false), Fields(noBranch));
        using var json = JsonDocument.Parse(await raw.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("repository_url").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("default_branch").ValueKind);
        Assert.Equal(("App", "APP", null, null, null, false), Stored(Assert.Single(await ProjectsAsync(fixture))));
    }

    [Fact]
    public async Task Update_archives_and_restores_a_project_and_its_list_and_board_behave_as_for_an_archive_from_the_web_ui()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP", RepositoryUrl = Repository });

        var archived = await ReadAsync(await PatchAsync(fixture, project, new { isArchived = true }));
        var listedByDefault = await fixture.Client.GetFromJsonAsync<List<ProjectSummaryDto>>("/api/projects");
        var listedWithArchived = await fixture.Client.GetFromJsonAsync<List<ProjectSummaryDto>>("/api/projects?includeArchived=true");
        var restored = await ReadAsync(await PatchAsync(fixture, project, new { isArchived = false }));

        Assert.Equal(("App", "APP", null, Repository, null, true), Fields(archived));
        Assert.Empty(listedByDefault!);
        Assert.True(Assert.Single(listedWithArchived!).IsArchived);
        Assert.False(restored.IsArchived);
        Assert.Single((await fixture.Client.GetFromJsonAsync<List<ProjectSummaryDto>>("/api/projects"))!);
    }

    [Fact]
    public async Task Update_of_an_unknown_project_is_refused_not_found()
    {
        using var fixture = await ApiFixture.CreateAsync();
        await SeedAsync(fixture, new Project { Name = "App", Key = "APP" });

        using var response = await PatchAsync(fixture, Guid.NewGuid(), new { name = "Renamed" });

        await AssertErrorAsync(response, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        Assert.Equal("App", Assert.Single(await ProjectsAsync(fixture)).Name);
    }

    // delete_project wins the race: the project is there when the update reads it and gone when it saves. The
    // partial update (MCP update_project) and the full update of the web UI share the path.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_of_a_project_deleted_after_it_was_read_is_refused_not_found_like_an_unknown_one(bool fullUpdate)
    {
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        using var client = api.CreateClient();
        var project = (await ProgramDependencyWiringTests.PostAsync<ProjectSummaryDto>(client, "/api/projects", new { name = "App", key = "APP" })).Id;
        var deleted = 0;
        api.BeforeNextSave = () =>
        {
            using var dbContext = api.OpenDatabase();
            deleted = dbContext.Projects.Where(candidate => candidate.Id == project).ExecuteDelete();
        };

        using var response = fullUpdate
            ? await client.PutAsJsonAsync($"/api/projects/{project}", new { name = "Renamed", key = "APP", isArchived = false })
            : await client.PatchAsJsonAsync($"/api/projects/{project}", new { name = "Renamed" });
        using var unknown = await client.PatchAsJsonAsync($"/api/projects/{project}", new { name = "Renamed" });

        Assert.Equal(1, deleted);
        await AssertErrorAsync(response, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        // The same body as for a project that is not there when it is read.
        await AssertErrorAsync(unknown, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Update_to_the_key_of_another_project_is_refused_conflict_and_a_name_another_project_has_is_allowed()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP" });
        await SeedAsync(fixture, new Project { Name = "Other", Key = "OTHER" });

        using var conflict = await PatchAsync(fixture, project, new { name = "Changed", key = "other" });
        var sameName = await ReadAsync(await PatchAsync(fixture, project, new { name = "Other", key = "app" }));

        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, KanbanErrorCodes.Conflict);
        // Only the key is unique; project names never were.
        Assert.Equal(("Other", "APP"), (sameName.Name, sameName.Key));
        Assert.Equal(["APP", "OTHER"], (await ProjectsAsync(fixture)).Select(stored => stored.Key).Order().ToArray());
    }

    [Fact]
    public async Task Update_that_changes_nothing_writes_nothing_and_sends_no_board_change()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP", DefaultBranch = "main" });
        using var changes = fixture.SubscribeToBoardChanges();

        var empty = await ReadAsync(await PatchAsync(fixture, project, new { }));
        var same = await ReadAsync(await PatchAsync(fixture, project, new { name = "App", defaultBranch = "main", isArchived = false, repositoryUrl = (string?)null }));
        var nothingSent = !changes.Reader.TryRead(out _);
        await ReadAsync(await PatchAsync(fixture, project, new { defaultBranch = "develop" }));

        Assert.Equal(Fields(empty), Fields(same));
        Assert.True(nothingSent);
        Assert.True(changes.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("""{"name":null}""")]
    [InlineData("""{"name":" "}""")]
    [InlineData("""{"key":null}""")]
    [InlineData("""{"key":""}""")]
    [InlineData("""{"isArchived":null}""")]
    [InlineData("""{"isArchived":"yes"}""")]
    [InlineData("""{"name":7}""")]
    [InlineData("""{"defaultBranch":["main"]}""")]
    [InlineData("""{"repository_url":"https://github.com/acme/app.git"}""")]
    [InlineData("""{"branch":"main"}""")]
    [InlineData("""["name"]""")]
    public async Task A_malformed_update_is_refused_invalid_argument_and_changes_nothing(string body)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP", RepositoryUrl = Repository, DefaultBranch = "main" });

        using var response = await fixture.Client.PatchAsync($"/api/projects/{project}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Equal(("App", "APP", null, Repository, "main", false), Stored(Assert.Single(await ProjectsAsync(fixture))));
    }

    [Fact]
    public async Task The_full_update_of_the_web_ui_keeps_the_repository_url_and_default_branch_and_reports_the_item_counts()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync(projectKey: "APP");
        var project = (await fixture.GetItemAsync(item)).ProjectId;
        await ReadAsync(await PatchAsync(fixture, project, new { repositoryUrl = Repository, defaultBranch = "main" }));

        using var response = await fixture.Client.PutAsJsonAsync($"/api/projects/{project}", new { name = "Renamed", key = "app", description = (string?)null, isArchived = true });
        using var unknown = await fixture.Client.PutAsJsonAsync($"/api/projects/{Guid.NewGuid()}", new { name = "Renamed", key = "X", isArchived = false });

        var updated = await ReadAsync(response);
        Assert.Equal(("Renamed", "APP", null, Repository, "main", true), Fields(updated));
        Assert.Equal((1, 1), (updated.TotalItems, updated.OpenItems));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_read_scope_token_can_list_projects_and_cannot_create_or_update_one()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var reader = await fixture.CreateCallerAsync("Reader", PersonalAccessTokenScopes.Read);
        var project = await SeedAsync(fixture, new Project { Name = "App", Key = "APP", RepositoryUrl = Repository });
        using var list = fixture.InternalRequest("/api/projects", reader.Token.Id, new { });
        list.Method = HttpMethod.Get;
        list.Content = null;
        using var patch = fixture.InternalRequest($"/api/projects/{project}", reader.Token.Id, new { name = "Renamed" });
        patch.Method = HttpMethod.Patch;

        using var listed = await fixture.Client.SendAsync(list);
        using var created = await fixture.PostAsync("/api/projects", reader, new { name = "New", key = "NEW" });
        using var updated = await fixture.Client.SendAsync(patch);

        Assert.Equal(Repository, Assert.Single((await listed.Content.ReadFromJsonAsync<List<ProjectSummaryDto>>())!).RepositoryUrl);
        await AssertErrorAsync(created, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        await AssertErrorAsync(updated, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        Assert.Equal("App", Assert.Single(await ProjectsAsync(fixture)).Name);
    }

    [Fact]
    public async Task Deleting_a_project_frees_its_idempotency_key()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var first = await ReadAsync(await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "App", key = "APP", idempotencyKey = "key" }));
        await fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            dbContext.Projects.Remove(await dbContext.Projects.SingleAsync());
            return await dbContext.SaveChangesAsync();
        });

        var second = await ReadAsync(await fixture.Client.PostAsJsonAsync("/api/projects", new { name = "App", key = "APP", idempotencyKey = "key" }));

        Assert.NotEqual(first.Id, second.Id);
        Assert.True(second.Created);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("run-42:step/3_done.v2~!", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a\u007f", false)]
    public void Idempotency_keys_are_printable_ascii_without_spaces(string key, bool valid) =>
        Assert.Equal(valid, IdempotencyKeys.IsValid(key));

    private static (string, string, string?, string?, string?, bool) Fields(ProjectSummaryDto project) =>
        (project.Name, project.Key, project.Description, project.RepositoryUrl, project.DefaultBranch, project.IsArchived);

    private static (string, string, string?, string?, string?, bool) Stored(Project project) =>
        (project.Name, project.Key, project.Description, project.RepositoryUrl, project.DefaultBranch, project.IsArchived);

    private static Task<HttpResponseMessage> PatchAsync(ApiFixture fixture, Guid project, object body) =>
        fixture.Client.PatchAsJsonAsync($"/api/projects/{project}", body);

    private static async Task<ProjectSummaryDto> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
            return JsonSerializer.Deserialize<ProjectSummaryDto>(body, JsonSerializerOptions.Web)!;
        }
    }

    private static Task<Guid> SeedAsync(ApiFixture fixture, Project project) =>
        fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            dbContext.Projects.Add(project);
            await dbContext.SaveChangesAsync();
            return project.Id;
        });

    private static Task<List<Project>> ProjectsAsync(ApiFixture fixture) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().Projects.AsNoTracking().ToListAsync());
}
