using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;

namespace KanbanBoard.Tests;

public sealed class WorkItemSearchTests
{
    [Fact]
    public async Task SearchAsync_matches_title_or_description_case_insensitively()
    {
        await using var dbContext = TestDb.CreateContext();
        var project = AddProject(dbContext, "ALPHA");
        var titleMatch = AddItem(dbContext, project, "Fix FOO parser", null);
        var descriptionMatch = AddItem(dbContext, project, "Other work", "Touches the Foo module");
        AddItem(dbContext, project, "Unrelated", "Nothing here");
        await dbContext.SaveChangesAsync();

        var results = await WorkItemSearch.SearchAsync(dbContext, "foo", null, null, CancellationToken.None);

        Assert.Equal(
            new[] { titleMatch.Id, descriptionMatch.Id }.OrderBy(id => id),
            results.Select(result => result.Id).OrderBy(id => id));
        var titleResult = results.Single(result => result.Id == titleMatch.Id);
        Assert.Equal("Fix FOO parser", titleResult.Title);
        Assert.Equal(WorkItemStatus.Backlog, titleResult.Status);
        Assert.Equal(project.Id, titleResult.ProjectId);
    }

    [Fact]
    public async Task SearchAsync_limits_to_project_when_given()
    {
        await using var dbContext = TestDb.CreateContext();
        var alpha = AddProject(dbContext, "ALPHA");
        var beta = AddProject(dbContext, "BETA");
        var alphaItem = AddItem(dbContext, alpha, "foo in alpha", null);
        AddItem(dbContext, beta, "foo in beta", null);
        await dbContext.SaveChangesAsync();

        var results = await WorkItemSearch.SearchAsync(dbContext, "foo", alpha.Id, null, CancellationToken.None);

        Assert.Equal(alphaItem.Id, Assert.Single(results).Id);
    }

    [Fact]
    public async Task SearchAsync_requires_every_word_and_includes_closed_items()
    {
        await using var dbContext = TestDb.CreateContext();
        var project = AddProject(dbContext, "ALPHA");
        var both = AddItem(dbContext, project, "Login bug", "Token expiry is wrong", WorkItemStatus.Closed);
        AddItem(dbContext, project, "Login page", "Styling");
        await dbContext.SaveChangesAsync();

        var results = await WorkItemSearch.SearchAsync(dbContext, "  login   TOKEN ", null, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(both.Id, result.Id);
        Assert.Equal(WorkItemStatus.Closed, result.Status);
    }

    [Fact]
    public async Task SearchAsync_caps_results_at_page_size()
    {
        await using var dbContext = TestDb.CreateContext();
        var project = AddProject(dbContext, "ALPHA");
        for (var index = 0; index < WorkItemSearch.MaxLimit + 5; index++)
        {
            AddItem(dbContext, project, $"foo {index}", null);
        }

        await dbContext.SaveChangesAsync();

        var defaultPage = await WorkItemSearch.SearchAsync(dbContext, "foo", null, null, CancellationToken.None);
        var smallPage = await WorkItemSearch.SearchAsync(dbContext, "foo", null, 3, CancellationToken.None);
        var oversizedPage = await WorkItemSearch.SearchAsync(dbContext, "foo", null, 10_000, CancellationToken.None);

        Assert.Equal(WorkItemSearch.DefaultLimit, defaultPage.Count);
        Assert.Equal(3, smallPage.Count);
        Assert.Equal(WorkItemSearch.MaxLimit, oversizedPage.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeTerms_rejects_blank_queries(string? query)
    {
        Assert.False(WorkItemSearch.TryNormalizeTerms(query, out _));
    }

    private static Project AddProject(KanbanDbContext dbContext, string key)
    {
        var project = new Project { Name = key, Key = key };
        dbContext.Projects.Add(project);
        return project;
    }

    private static WorkItem AddItem(
        KanbanDbContext dbContext,
        Project project,
        string title,
        string? description,
        WorkItemStatus status = WorkItemStatus.Backlog)
    {
        var item = new WorkItem
        {
            ProjectId = project.Id,
            Title = title,
            Description = description,
            Type = WorkItemType.Story,
            Status = status
        };
        dbContext.WorkItems.Add(item);
        return item;
    }
}
