using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static KanbanBoard.Tests.WorkItemClaimApiTests;

namespace KanbanBoard.Tests;

// Runs the dependency and work item read endpoints against SQLite on a file, like WorkItemClaimApiTests:
// real SQL, real foreign keys, and one connection per request so concurrent adds genuinely race.
public sealed class WorkItemDependencyApiTests
{
    [Fact]
    public async Task Reverse_of_an_existing_dependency_is_refused_cycle_and_writes_nothing()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");

        var added = await AddAsync(fixture, blocked: b, blocker: a);
        var reversed = await AddAsync(fixture, blocked: a, blocker: b);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        await AssertErrorAsync(reversed, HttpStatusCode.Conflict, KanbanErrorCodes.Cycle);
        Assert.Equal([(b, a)], await EdgesAsync(fixture));
    }

    [Fact]
    public async Task Edge_that_closes_a_three_item_loop_is_refused_cycle_and_writes_nothing()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        var c = await fixture.AddItemAsync("C");
        await AddAsync(fixture, blocked: b, blocker: a);
        await AddAsync(fixture, blocked: c, blocker: b);

        var closing = await AddAsync(fixture, blocked: a, blocker: c);

        await AssertErrorAsync(closing, HttpStatusCode.Conflict, KanbanErrorCodes.Cycle);
        Assert.Equal(new[] { (b, a), (c, b) }.Order(), await EdgesAsync(fixture));
    }

    [Fact]
    public async Task Self_dependency_is_refused_cycle_and_writes_nothing()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");

        var self = await AddAsync(fixture, blocked: a, blocker: a);

        await AssertErrorAsync(self, HttpStatusCode.Conflict, KanbanErrorCodes.Cycle);
        Assert.Empty(await EdgesAsync(fixture));
    }

    [Fact]
    public async Task Adding_the_same_dependency_twice_succeeds_and_stores_one_edge()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        await AddAsync(fixture, blocked: b, blocker: a);

        var again = await AddAsync(fixture, blocked: b, blocker: a);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(a, Assert.Single((await again.Content.ReadFromJsonAsync<WorkItemDto>())!.Blockers).Id);
        Assert.Equal([(b, a)], await EdgesAsync(fixture));
    }

    [Fact]
    public async Task Remove_deletes_the_edge_and_is_a_no_op_when_repeated()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        await AddAsync(fixture, blocked: b, blocker: a);

        var removed = await RemoveAsync(fixture, blocked: b, blocker: a);
        var removedAgain = await RemoveAsync(fixture, blocked: b, blocker: a);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var removedDto = await removed.Content.ReadFromJsonAsync<WorkItemDto>();
        Assert.Equal(b, removedDto!.Id);
        Assert.Empty(removedDto.Blockers);
        Assert.Equal(HttpStatusCode.OK, removedAgain.StatusCode);
        Assert.Empty(await EdgesAsync(fixture));
        // With the edge gone the reverse direction is no longer a cycle.
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(fixture, blocked: a, blocker: b)).StatusCode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Unknown_item_on_either_side_is_refused_not_found(bool blockedExists, bool blockerExists)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var blocked = blockedExists ? await fixture.AddItemAsync("A") : Guid.NewGuid();
        var blocker = blockerExists ? await fixture.AddItemAsync("B") : Guid.NewGuid();

        var added = await AddAsync(fixture, blocked, blocker);
        var selfOnUnknown = await AddAsync(fixture, Guid.Empty, Guid.Empty);
        var removed = await RemoveAsync(fixture, blocked, blocker);

        await AssertErrorAsync(added, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        await AssertErrorAsync(selfOnUnknown, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        await AssertErrorAsync(removed, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        Assert.Empty(await EdgesAsync(fixture));
    }

    [Fact]
    public async Task Get_and_list_return_blockers_and_dependents_with_their_current_status()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        // Dependencies are not limited to one project.
        var c = await fixture.AddItemAsync("C", projectKey: "OTHER");
        await AddAsync(fixture, blocked: b, blocker: a);
        var added = await AddAsync(fixture, blocked: c, blocker: b);
        await SetStatusAsync(fixture, a, WorkItemStatus.Done);
        await SetStatusAsync(fixture, c, WorkItemStatus.InProgress);

        var addedDto = await added.Content.ReadFromJsonAsync<WorkItemDto>();
        var rawB = await fixture.Client.GetStringAsync($"/api/items/{b}");
        var gotB = JsonSerializer.Deserialize<WorkItemDto>(rawB, JsonSerializerOptions.Web)!;
        var listed = (await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>("/api/items"))!.ToDictionary(item => item.Id);

        Assert.Equal([new WorkItemDependencyRefDto(b, "B", WorkItemStatus.Backlog)], addedDto!.Blockers);
        Assert.Equal([new WorkItemDependencyRefDto(a, "A", WorkItemStatus.Done)], gotB.Blockers);
        Assert.Equal([new WorkItemDependencyRefDto(c, "C", WorkItemStatus.InProgress)], gotB.Dependents);
        using (var json = JsonDocument.Parse(rawB))
        {
            var blocker = Assert.Single(json.RootElement.GetProperty("blockers").EnumerateArray());
            Assert.Equal(["id", "status", "title"], blocker.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(1, json.RootElement.GetProperty("dependents").GetArrayLength());
        }

        Assert.Equal(3, listed.Count);
        Assert.Empty(listed[a].Blockers);
        Assert.Equal([new WorkItemDependencyRefDto(b, "B", WorkItemStatus.Backlog)], listed[a].Dependents);
        Assert.Equal(gotB.Blockers, listed[b].Blockers);
        Assert.Equal(gotB.Dependents, listed[b].Dependents);
        Assert.Equal([new WorkItemDependencyRefDto(b, "B", WorkItemStatus.Backlog)], listed[c].Blockers);
        Assert.Empty(listed[c].Dependents);
    }

    [Fact]
    public async Task Unblocked_only_returns_an_item_only_when_every_blocker_is_done()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        var c = await fixture.AddItemAsync("C");
        var other = await fixture.AddItemAsync("Other blocker of C");
        await AddAsync(fixture, blocked: c, blocker: b);
        await AddAsync(fixture, blocked: b, blocker: a);

        var everything = await ListIdsAsync(fixture, "/api/items");
        var chain = await ListIdsAsync(fixture, "/api/items?unblockedOnly=true");
        await SetStatusAsync(fixture, a, WorkItemStatus.InProgress);
        var blockerStarted = await ListIdsAsync(fixture, "/api/items?unblockedOnly=true");
        await SetStatusAsync(fixture, a, WorkItemStatus.Done);
        var blockerDone = await ListIdsAsync(fixture, "/api/items?unblockedOnly=true");
        var blockerDoneBacklogOnly = await ListIdsAsync(fixture, "/api/items?unblockedOnly=true&status=Backlog");

        // C then gets a second blocker: one Done blocker out of two is not enough.
        await SetStatusAsync(fixture, b, WorkItemStatus.Done);
        await AddAsync(fixture, blocked: c, blocker: other);
        var oneOfTwoDone = await ListIdsAsync(fixture, "/api/items?unblockedOnly=true");
        await SetStatusAsync(fixture, other, WorkItemStatus.Done);
        var bothDone = await ListIdsAsync(fixture, "/api/items?unblockedOnly=true");

        Assert.Equal(new[] { a, b, c, other }.Order(), everything);
        Assert.Equal(new[] { a, other }.Order(), chain);
        Assert.Equal(new[] { a, other }.Order(), blockerStarted);
        Assert.Equal(new[] { a, b, other }.Order(), blockerDone);
        Assert.Equal(new[] { b, other }.Order(), blockerDoneBacklogOnly);
        Assert.DoesNotContain(c, oneOfTwoDone);
        Assert.Contains(c, bothDone);
    }

    [Fact]
    public async Task Deleting_an_item_removes_its_edges_in_both_directions()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        var c = await fixture.AddItemAsync("C");
        await AddAsync(fixture, blocked: b, blocker: a);
        await AddAsync(fixture, blocked: c, blocker: b);

        // What DELETE /api/items/{id} does.
        await fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            dbContext.WorkItems.Remove(await dbContext.WorkItems.SingleAsync(item => item.Id == b));
            return await dbContext.SaveChangesAsync();
        });

        Assert.Empty(await EdgesAsync(fixture));
        Assert.Empty((await fixture.Client.GetFromJsonAsync<WorkItemDto>($"/api/items/{a}"))!.Dependents);
        Assert.Empty((await fixture.Client.GetFromJsonAsync<WorkItemDto>($"/api/items/{c}"))!.Blockers);
        Assert.Contains(c, await ListIdsAsync(fixture, "/api/items?unblockedOnly=true"));
    }

    [Fact]
    public async Task Concurrent_adds_that_together_form_a_cycle_never_all_succeed()
    {
        using var fixture = await ApiFixture.CreateAsync();
        const int ringSize = 6;

        // Each round asks, at once, for every edge of a ring. No two requests name the same pair, and each one
        // alone is fine, so a check that is not serialised with its insert lets all of them through some of the time.
        for (var round = 0; round < 8; round++)
        {
            var ring = new List<Guid>();
            for (var index = 0; index < ringSize; index++)
            {
                ring.Add(await fixture.AddItemAsync($"R{round}-{index}"));
            }

            using var start = new SemaphoreSlim(0);
            var attempts = Enumerable.Range(0, ringSize)
                .Select(index => Task.Run(async () =>
                {
                    await start.WaitAsync();
                    return await AddAsync(fixture, blocked: ring[index], blocker: ring[(index + 1) % ringSize]);
                }))
                .ToList();
            start.Release(ringSize);
            var responses = await Task.WhenAll(attempts);

            var refused = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.OK);
            await AssertErrorAsync(refused, HttpStatusCode.Conflict, KanbanErrorCodes.Cycle);
            var edges = (await EdgesAsync(fixture)).Where(edge => ring.Contains(edge.Blocked)).ToList();
            Assert.Equal(ringSize - 1, edges.Count);
        }
    }

    private static Task<HttpResponseMessage> AddAsync(ApiFixture fixture, Guid blocked, Guid blocker) =>
        fixture.Client.PostAsJsonAsync($"/api/items/{blocked}/dependencies", new AddWorkItemDependencyRequest(blocker));

    private static Task<HttpResponseMessage> RemoveAsync(ApiFixture fixture, Guid blocked, Guid blocker) =>
        fixture.Client.DeleteAsync($"/api/items/{blocked}/dependencies/{blocker}");

    private static async Task<List<Guid>> ListIdsAsync(ApiFixture fixture, string path) =>
        (await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>(path))!.Select(item => item.Id).Order().ToList();

    private static Task<List<(Guid Blocked, Guid Blocker)>> EdgesAsync(ApiFixture fixture) =>
        fixture.WithServicesAsync(async services =>
            (await services.GetRequiredService<KanbanDbContext>().WorkItemDependencies.AsNoTracking().ToListAsync())
            .Select(edge => (edge.BlockedItemId, edge.BlockerItemId))
            .Order()
            .ToList());

    private static Task<int> SetStatusAsync(ApiFixture fixture, Guid itemId, WorkItemStatus status) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().WorkItems
            .Where(item => item.Id == itemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, status)));
}
