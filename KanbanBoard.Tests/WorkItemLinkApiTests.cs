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

// Runs the link and work item read endpoints against SQLite on a file, like WorkItemDependencyApiTests:
// real SQL, a real unique index and foreign key, and one connection per request so concurrent adds genuinely race.
public sealed class WorkItemLinkApiTests
{
    private const string PrUrl = "https://github.com/acme/app/pull/42";

    [Theory]
    [InlineData("pr")]
    [InlineData("branch")]
    [InlineData("other")]
    public async Task Add_accepts_each_kind_and_returns_the_item_with_the_link(string kind)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();

        var response = await AddAsync(fixture, item, kind, PrUrl);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);
        var dto = JsonSerializer.Deserialize<WorkItemDto>(raw, JsonSerializerOptions.Web)!;
        Assert.Equal(item, dto.Id);
        var link = Assert.Single(dto.Links);
        Assert.Equal((kind, PrUrl, fixture.Clock.Now), (link.Kind, link.Url, link.CreatedAtUtc));
        Assert.Equal([(item, link.Id, kind, PrUrl)], await LinksAsync(fixture));
        using var json = JsonDocument.Parse(raw);
        var wire = Assert.Single(json.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal(["created_at_utc", "id", "kind", "url"], wire.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("pull_request")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Add_with_any_other_kind_is_refused_invalid_argument_and_writes_nothing(string? kind)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();

        var response = await AddAsync(fixture, item, kind, PrUrl);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(await LinksAsync(fixture));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("github.com/acme/app/pull/42")]
    [InlineData("/acme/app/pull/42")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    public async Task Add_with_a_url_that_is_not_absolute_http_or_https_is_refused_invalid_argument(string? url)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();

        var response = await AddAsync(fixture, item, "pr", url);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(await LinksAsync(fixture));
    }

    [Fact]
    public async Task Url_length_is_capped_and_the_longest_allowed_url_can_be_found_again()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();
        var longest = "https://example.com/" + new string('a', WorkItemLinkLimits.MaxUrlLength - "https://example.com/".Length);

        var tooLong = await AddAsync(fixture, item, "other", longest + "a");
        var added = await AddAsync(fixture, item, "other", longest);

        await AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal([item], await FindIdsAsync(fixture, longest));
        // One character less is another URL.
        Assert.Empty(await FindIdsAsync(fixture, longest[..^1]));
    }

    [Fact]
    public async Task Adding_the_same_kind_and_url_twice_returns_the_existing_link_and_stores_one()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();
        var first = await ReadAsync(await AddAsync(fixture, item, "pr", PrUrl));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // Kind ignores case and both ignore surrounding whitespace, so this is the same link.
        var again = await ReadAsync(await AddAsync(fixture, item, " PR ", $" {PrUrl} "));

        Assert.Equal(first.Links, again.Links);
        Assert.Single(await LinksAsync(fixture));
        // Nothing changed, so the item was not stamped again.
        Assert.Equal(first.UpdatedAtUtc, again.UpdatedAtUtc);
    }

    [Fact]
    public async Task Concurrent_identical_adds_all_succeed_and_store_exactly_one_link()
    {
        using var fixture = await ApiFixture.CreateAsync();

        for (var round = 0; round < 8; round++)
        {
            var item = await fixture.AddItemAsync($"R{round}");
            var url = $"{PrUrl}{round}";
            using var start = new SemaphoreSlim(0);
            var attempts = Enumerable.Range(0, 6)
                .Select(_ => Task.Run(async () =>
                {
                    await start.WaitAsync();
                    return await AddAsync(fixture, item, "pr", url);
                }))
                .ToList();
            start.Release(attempts.Count);
            var responses = await Task.WhenAll(attempts);

            var linkIds = new HashSet<Guid>();
            foreach (var response in responses)
            {
                Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                linkIds.Add(Assert.Single((await ReadAsync(response)).Links).Id);
            }

            Assert.Single(linkIds);
            Assert.Single(await LinksAsync(fixture), link => link.Item == item);
        }
    }

    [Fact]
    public async Task One_url_can_be_on_an_item_under_several_kinds_and_on_several_items()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B", projectKey: "OTHER");
        await AddAsync(fixture, a, "pr", PrUrl);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));

        var twoKinds = await ReadAsync(await AddAsync(fixture, a, "other", PrUrl));
        await AddAsync(fixture, b, "pr", PrUrl);

        // Oldest first.
        Assert.Equal(["pr", "other"], twoKinds.Links.Select(link => link.Kind).ToArray());
        Assert.Equal(3, (await LinksAsync(fixture)).Count);
        Assert.Equal(new[] { a, b }.Order(), await FindIdsAsync(fixture, PrUrl));
    }

    [Fact]
    public async Task Get_list_paged_list_claim_and_dependency_responses_return_the_links_of_each_item()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("factory");
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        var c = await fixture.AddItemAsync("C");
        var linkA = Assert.Single((await ReadAsync(await AddAsync(fixture, a, "pr", PrUrl))).Links);
        var linkB = Assert.Single((await ReadAsync(await AddAsync(fixture, b, "branch", "https://github.com/acme/app/tree/feature"))).Links);

        var got = await fixture.Client.GetFromJsonAsync<WorkItemDto>($"/api/items/{a}");
        var listed = (await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>("/api/items"))!.ToDictionary(item => item.Id);
        var paged = new Dictionary<Guid, WorkItemDto>();
        string? cursor = null;
        do
        {
            var page = await fixture.Client.GetFromJsonAsync<WorkItemPageDto>(
                $"/api/items?limit=1{(cursor is null ? string.Empty : $"&cursor={cursor}")}", JsonSerializerOptions.Web);
            paged.Add(Assert.Single(page!.Items).Id, page.Items[0]);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        var claimed = await ReadAsync(await fixture.ClaimAsync(a, caller, "run-1", 60));
        var blocked = await ReadAsync(await fixture.Client.PostAsJsonAsync($"/api/items/{b}/dependencies", new AddWorkItemDependencyRequest(a)));

        Assert.Equal([linkA], got!.Links);
        Assert.Equal(3, listed.Count);
        Assert.Equal([linkA], listed[a].Links);
        Assert.Equal([linkB], listed[b].Links);
        Assert.Empty(listed[c].Links);
        Assert.Equal(3, paged.Count);
        Assert.Equal([linkA], paged[a].Links);
        Assert.Equal([linkB], paged[b].Links);
        Assert.Empty(paged[c].Links);
        Assert.Equal([linkA], claimed.Links);
        Assert.Equal([linkB], blocked.Links);
    }

    [Fact]
    public async Task Remove_by_link_id_removes_that_link_and_is_a_no_op_when_repeated()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();
        var pr = Assert.Single((await ReadAsync(await AddAsync(fixture, item, "pr", PrUrl))).Links);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await AddAsync(fixture, item, "other", PrUrl);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var removed = await ReadAsync(await fixture.Client.DeleteAsync($"/api/items/{item}/links?linkId={pr.Id}"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var removedAgain = await ReadAsync(await fixture.Client.DeleteAsync($"/api/items/{item}/links?linkId={pr.Id}"));

        Assert.Equal(item, removed.Id);
        Assert.Equal("other", Assert.Single(removed.Links).Kind);
        Assert.Equal(removed.Links, removedAgain.Links);
        Assert.Single(await LinksAsync(fixture));
        // The removal stamped the item; the repeat changed nothing and did not.
        Assert.Equal(fixture.Clock.Now.AddMinutes(-1), removed.UpdatedAtUtc);
        Assert.Equal(removed.UpdatedAtUtc, removedAgain.UpdatedAtUtc);
    }

    [Fact]
    public async Task Remove_by_kind_and_url_removes_only_the_link_of_that_kind_on_that_item()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        await AddAsync(fixture, a, "pr", PrUrl);
        await AddAsync(fixture, a, "other", PrUrl);
        await AddAsync(fixture, b, "pr", PrUrl);

        var removed = await ReadAsync(await RemoveAsync(fixture, a, "PR", PrUrl));
        var differentCase = await ReadAsync(await RemoveAsync(fixture, a, "other", PrUrl.ToUpperInvariant()));
        var removedAgain = await RemoveAsync(fixture, a, "pr", PrUrl);

        Assert.Equal("other", Assert.Single(removed.Links).Kind);
        // A URL that differs only in case is another URL, so nothing was removed.
        Assert.Single(differentCase.Links);
        Assert.Equal(HttpStatusCode.OK, removedAgain.StatusCode);
        Assert.Equal(new[] { (a, "other"), (b, "pr") }.Order(), (await LinksAsync(fixture)).Select(link => (link.Item, link.Kind)).Order());
    }

    [Fact]
    public async Task Remove_with_the_link_id_of_another_item_changes_nothing()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        var linkOnA = Assert.Single((await ReadAsync(await AddAsync(fixture, a, "pr", PrUrl))).Links);

        var response = await fixture.Client.DeleteAsync($"/api/items/{b}/links?linkId={linkOnA.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(a, Assert.Single(await LinksAsync(fixture)).Item);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?kind=pr")]
    [InlineData("?url=https%3A%2F%2Fexample.com%2F")]
    [InlineData("?kind=pr&url=")]
    [InlineData("?linkId=11111111-1111-1111-1111-111111111111&kind=pr&url=https%3A%2F%2Fexample.com%2F")]
    [InlineData("?linkId=11111111-1111-1111-1111-111111111111&url=https%3A%2F%2Fexample.com%2F")]
    [InlineData("?kind=issue&url=https%3A%2F%2Fexample.com%2F")]
    public async Task Remove_that_does_not_address_one_link_is_refused_invalid_argument(string query)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();
        await AddAsync(fixture, item, "pr", "https://example.com/");

        var response = await fixture.Client.DeleteAsync($"/api/items/{item}/links{query}");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Single(await LinksAsync(fixture));
    }

    [Fact]
    public async Task Unknown_item_is_refused_not_found_by_add_and_remove()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var unknown = Guid.NewGuid();

        var added = await AddAsync(fixture, unknown, "pr", PrUrl);
        var removedByUrl = await RemoveAsync(fixture, unknown, "pr", PrUrl);
        var removedById = await fixture.Client.DeleteAsync($"/api/items/{unknown}/links?linkId={Guid.NewGuid()}");

        await AssertErrorAsync(added, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        await AssertErrorAsync(removedByUrl, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        await AssertErrorAsync(removedById, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        Assert.Empty(await LinksAsync(fixture));
    }

    [Fact]
    public async Task Find_returns_exactly_the_items_that_carry_the_url_and_an_empty_list_for_an_unknown_url()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var closed = await fixture.AddItemAsync("Closed", projectKey: "OTHER");
        var elsewhere = await fixture.AddItemAsync("Elsewhere");
        var removed = await fixture.AddItemAsync("Removed");
        await fixture.AddItemAsync("No links");
        var linkA = Assert.Single((await ReadAsync(await AddAsync(fixture, a, "pr", PrUrl))).Links);
        await AddAsync(fixture, closed, "other", PrUrl);
        await AddAsync(fixture, elsewhere, "pr", PrUrl + "0");
        await AddAsync(fixture, removed, "pr", PrUrl);
        await RemoveAsync(fixture, removed, "pr", PrUrl);
        await fixture.WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().WorkItems
            .Where(item => item.Id == closed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, WorkItemStatus.Closed)));

        var found = (await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items/by-link?url={Uri.EscapeDataString(PrUrl)}"))!;
        var unknown = await fixture.Client.GetAsync($"/api/items/by-link?url={Uri.EscapeDataString("https://github.com/acme/app/pull/999")}");
        var notAUrl = await fixture.Client.GetAsync("/api/items/by-link?url=value");

        Assert.Equal(new[] { a, closed }.Order(), found.Select(item => item.Id).Order());
        Assert.Equal([linkA], found.Single(item => item.Id == a).Links);
        Assert.Equal("A", found.Single(item => item.Id == a).Title);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal("[]", await unknown.Content.ReadAsStringAsync());
        Assert.Equal("[]", await notAUrl.Content.ReadAsStringAsync());
        // Exact match: a different case, a trailing slash or a prefix is another URL.
        Assert.Empty(await FindIdsAsync(fixture, PrUrl.ToUpperInvariant()));
        Assert.Empty(await FindIdsAsync(fixture, PrUrl.Replace("acme", "Acme", StringComparison.Ordinal)));
        Assert.Empty(await FindIdsAsync(fixture, PrUrl + "/"));
        Assert.Empty(await FindIdsAsync(fixture, PrUrl[..^1]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?url=")]
    [InlineData("?url=%20")]
    public async Task Find_without_a_url_is_refused_invalid_argument(string query)
    {
        using var fixture = await ApiFixture.CreateAsync();

        var response = await fixture.Client.GetAsync($"/api/items/by-link{query}");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Adding_a_link_stamps_the_item_so_updated_since_reports_it()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync("Linked");
        var other = await fixture.AddItemAsync("Other");
        var before = (await fixture.GetItemAsync(other)).UpdatedAtUtc;
        // The items were created on the system clock; the fixture's clock stamps the link.
        fixture.Clock.Advance(TimeSpan.FromDays(3650));

        var added = await ReadAsync(await AddAsync(fixture, item, "pr", PrUrl));
        var changed = await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>(
            $"/api/items?updatedSince={Uri.EscapeDataString(fixture.Clock.Now.ToString("O"))}");

        Assert.Equal(fixture.Clock.Now, added.UpdatedAtUtc);
        Assert.Equal(item, Assert.Single(changed!).Id);
        Assert.Equal(before, (await fixture.GetItemAsync(other)).UpdatedAtUtc);
    }

    [Fact]
    public async Task Deleting_an_item_deletes_its_links()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var a = await fixture.AddItemAsync("A");
        var b = await fixture.AddItemAsync("B");
        await AddAsync(fixture, a, "pr", PrUrl);
        await AddAsync(fixture, a, "branch", PrUrl);
        await AddAsync(fixture, b, "pr", PrUrl);

        // What DELETE /api/items/{id} does.
        await fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            dbContext.WorkItems.Remove(await dbContext.WorkItems.SingleAsync(item => item.Id == a));
            return await dbContext.SaveChangesAsync();
        });

        Assert.Equal(b, Assert.Single(await LinksAsync(fixture)).Item);
        Assert.Equal([b], await FindIdsAsync(fixture, PrUrl));
    }

    private static Task<HttpResponseMessage> AddAsync(ApiFixture fixture, Guid item, string? kind, string? url) =>
        fixture.Client.PostAsJsonAsync($"/api/items/{item}/links", new { kind, url });

    private static Task<HttpResponseMessage> RemoveAsync(ApiFixture fixture, Guid item, string kind, string url) =>
        fixture.Client.DeleteAsync($"/api/items/{item}/links?kind={Uri.EscapeDataString(kind)}&url={Uri.EscapeDataString(url)}");

    private static async Task<WorkItemDto> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<WorkItemDto>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task<List<Guid>> FindIdsAsync(ApiFixture fixture, string url) =>
        (await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>($"/api/items/by-link?url={Uri.EscapeDataString(url)}"))!
            .Select(item => item.Id).Order().ToList();

    private static Task<List<(Guid Item, Guid Id, string Kind, string Url)>> LinksAsync(ApiFixture fixture) =>
        fixture.WithServicesAsync(async services =>
            (await services.GetRequiredService<KanbanDbContext>().WorkItemLinks.AsNoTracking().ToListAsync())
            .Select(link => (link.WorkItemId, link.Id, link.Kind, link.Url))
            .ToList());
}
