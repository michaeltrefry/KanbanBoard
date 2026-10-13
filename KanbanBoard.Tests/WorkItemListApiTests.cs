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

// The filters and the cursor of GET /api/items against SQLite on a file, like WorkItemClaimApiTests.
public sealed class WorkItemListApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Label_filter_matches_a_whole_label_and_ignores_case()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var api = await AddAsync(fixture, "api", item => item.Labels = "api");
        var rapid = await AddAsync(fixture, "rapid", item => item.Labels = "rapid");
        var first = await AddAsync(fixture, "first of two", item => item.Labels = "API,backend");
        var last = await AddAsync(fixture, "last of three", item => item.Labels = "ui,backend,Api");
        var longer = await AddAsync(fixture, "longer", item => item.Labels = "backend,api-v2");
        var wildcards = await AddAsync(fixture, "wildcards", item => item.Labels = "a_i,100%");
        await AddAsync(fixture, "unlabelled");

        Assert.Equal(Sorted(api, first, last), await ListIdsAsync(fixture, "/api/items?label=api"));
        Assert.Equal(Sorted(api, first, last), await ListIdsAsync(fixture, "/api/items?label=%20API%20"));
        Assert.Equal(Sorted(rapid), await ListIdsAsync(fixture, "/api/items?label=rapid"));
        Assert.Equal(Sorted(first, last, longer), await ListIdsAsync(fixture, "/api/items?label=backend"));
        Assert.Equal(Sorted(longer), await ListIdsAsync(fixture, "/api/items?label=api-v2"));
        // The characters that are wildcards in a SQL LIKE pattern are ordinary label characters.
        Assert.Equal(Sorted(wildcards), await ListIdsAsync(fixture, "/api/items?label=a_i"));
        Assert.Equal(Sorted(wildcards), await ListIdsAsync(fixture, "/api/items?label=100%25"));
        Assert.Empty(await ListIdsAsync(fixture, "/api/items?label=a%25"));
        Assert.Empty(await ListIdsAsync(fixture, "/api/items?label=pi"));
    }

    [Fact]
    public async Task Updated_since_returns_items_updated_at_or_after_the_timestamp()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var old = await AddAsync(fixture, "old", item => item.UpdatedAtUtc = Start);
        var at = await AddAsync(fixture, "at", item => item.UpdatedAtUtc = Start.AddHours(1));
        var after = await AddAsync(fixture, "after", item => item.UpdatedAtUtc = Start.AddHours(1).AddTicks(1234567));

        Assert.Equal(Sorted(old, at, after), await ListIdsAsync(fixture, "/api/items?updatedSince=2026-10-01T08:00:00Z"));
        Assert.Equal(Sorted(at, after), await ListIdsAsync(fixture, "/api/items?updatedSince=2026-10-01T09:00:00Z"));
        // An offset other than UTC, and no offset at all (read as UTC).
        Assert.Equal(Sorted(at, after), await ListIdsAsync(fixture, "/api/items?updatedSince=2026-10-01T11:00:00%2B02:00"));
        Assert.Equal(Sorted(at, after), await ListIdsAsync(fixture, "/api/items?updatedSince=2026-10-01T09:00:00"));
        // The stamp of an item, as a write response reports it, selects that item.
        Assert.Equal(Sorted(after), await ListIdsAsync(fixture, "/api/items?updatedSince=2026-10-01T09:00:00.1234567%2B00:00"));
        Assert.Empty(await ListIdsAsync(fixture, "/api/items?updatedSince=2026-10-01T09:00:01Z"));
    }

    [Fact]
    public async Task Claim_state_follows_the_clock_without_any_write_to_the_item()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var free = await AddAsync(fixture, "never claimed");
        var held = await AddAsync(fixture, "claimed for an hour");
        var lapsing = await AddAsync(fixture, "claimed for a minute");
        var released = await AddAsync(fixture, "claimed and released");
        Assert.Equal(HttpStatusCode.OK, (await fixture.ClaimAsync(held, caller, "run-1", 3600)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.ClaimAsync(lapsing, caller, "run-1", 60)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.ClaimAsync(released, caller, "run-1", 3600)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync($"/api/items/{released}/claim/release", caller, new ReleaseWorkItemClaimRequest("run-1"))).StatusCode);
        var stampedBefore = (await fixture.GetItemAsync(lapsing)).UpdatedAtUtc;

        var unclaimedBefore = await ListIdsAsync(fixture, "/api/items?claimState=unclaimed");
        var claimedBefore = await ListIdsAsync(fixture, "/api/items?claimState=claimed");
        var expiredBefore = await ListIdsAsync(fixture, "/api/items?claimState=expired");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var unclaimedAfter = await ListIdsAsync(fixture, "/api/items?claimState=Unclaimed");
        var claimedAfter = await ListIdsAsync(fixture, "/api/items?claimState=claimed");
        var expiredAfter = await ListIdsAsync(fixture, "/api/items?claimState=expired");

        Assert.Equal(Sorted(free, released), unclaimedBefore);
        Assert.Equal(Sorted(held, lapsing), claimedBefore);
        Assert.Empty(expiredBefore);
        // Nothing wrote to the lapsed item; it is unclaimed because the clock moved.
        Assert.Equal(Sorted(free, released, lapsing), unclaimedAfter);
        Assert.Equal(Sorted(held), claimedAfter);
        Assert.Equal(Sorted(lapsing), expiredAfter);
        // Claiming, releasing and lapsing are not changes to the item's content.
        Assert.Equal(stampedBefore, (await fixture.GetItemAsync(lapsing)).UpdatedAtUtc);
        // The lapsed item reads as unclaimed, as it does everywhere else.
        var listed = await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>("/api/items?claimState=expired");
        Assert.Null(Assert.Single(listed!).ClaimMarker);
    }

    [Fact]
    public async Task Claim_and_release_leave_the_content_timestamp_alone()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var item = await AddAsync(fixture, "item", candidate => candidate.UpdatedAtUtc = Start);

        await fixture.ClaimAsync(item, caller, "run-1", 60);
        await fixture.PostAsync($"/api/items/{item}/claim/renew", caller, new ClaimWorkItemRequest("run-1", 120));
        await fixture.PostAsync($"/api/items/{item}/claim/release", caller, new ReleaseWorkItemClaimRequest("run-1"));

        Assert.Equal(Start, (await fixture.GetItemAsync(item)).UpdatedAtUtc);
    }

    [Fact]
    public async Task Paging_returns_every_matching_item_exactly_once_oldest_first()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var expected = new List<Guid>();
        for (var index = 0; index < 11; index++)
        {
            // Items 3 to 6 share one creation time, so a page boundary falls inside a tie.
            var createdAtUtc = Start.AddMinutes(index is >= 3 and <= 6 ? 3 : index);
            var matches = index != 8;
            var id = await AddAsync(fixture, $"item {index}", item =>
            {
                item.CreatedAtUtc = createdAtUtc;
                item.Labels = matches ? "factory" : "other";
                item.Status = index % 2 == 0 ? WorkItemStatus.Ready : WorkItemStatus.Backlog;
                item.Order = 20 - index;
            });
            if (matches)
            {
                expected.Add(id);
            }
        }

        var pages = await WalkAsync(fixture, "/api/items?label=factory&limit=3");

        Assert.Equal([3, 3, 3, 1], pages.Select(page => page.Items.Count));
        var walked = pages.SelectMany(page => page.Items).ToList();
        Assert.Equal(expected.Order(), walked.Select(item => item.Id).Order());
        Assert.Equal(walked.OrderBy(item => item.CreatedAtUtc).Select(item => item.CreatedAtUtc), walked.Select(item => item.CreatedAtUtc));
        Assert.All(pages.SkipLast(1), page => Assert.False(string.IsNullOrEmpty(page.NextCursor)));
        Assert.Null(pages[^1].NextCursor);
    }

    [Fact]
    public async Task Last_page_that_is_exactly_full_has_no_next_cursor_and_an_empty_result_is_an_empty_page()
    {
        using var fixture = await ApiFixture.CreateAsync();
        for (var index = 0; index < 4; index++)
        {
            await AddAsync(fixture, $"item {index}", item => item.CreatedAtUtc = Start.AddMinutes(index));
        }

        var pages = await WalkAsync(fixture, "/api/items?limit=2");
        var none = await GetPageAsync(fixture, "/api/items?limit=2&label=missing");
        // A cursor alone pages too, with the default page size.
        var rest = await GetPageAsync(fixture, $"/api/items?cursor={pages[0].NextCursor}");

        Assert.Equal([2, 2], pages.Select(page => page.Items.Count));
        Assert.Null(pages[^1].NextCursor);
        Assert.Empty(none.Items);
        Assert.Null(none.NextCursor);
        Assert.Equal(pages[1].Items.Select(item => item.Id), rest.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Paging_is_not_disturbed_by_items_that_are_moved_and_edited_between_pages()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var ids = new List<Guid>();
        for (var index = 0; index < 9; index++)
        {
            ids.Add(await AddAsync(fixture, $"item {index}", item =>
            {
                item.CreatedAtUtc = Start.AddMinutes(index);
                item.Order = index;
            }));
        }

        var seen = new List<Guid>();
        var page = await GetPageAsync(fixture, "/api/items?limit=4");
        seen.AddRange(page.Items.Select(item => item.Id));
        // An item already returned goes to the end of the board order and one not yet returned goes to the front;
        // both are edited. Board-order paging would return the first twice and skip the second.
        await fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            var returned = await dbContext.WorkItems.SingleAsync(item => item.Id == ids[0]);
            returned.Status = WorkItemStatus.InProgress;
            returned.Order = 99;
            returned.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var pending = await dbContext.WorkItems.SingleAsync(item => item.Id == ids[8]);
            pending.Order = -1;
            pending.Title = "edited";
            pending.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return await dbContext.SaveChangesAsync();
        });
        while (page.NextCursor is not null)
        {
            page = await GetPageAsync(fixture, $"/api/items?limit=4&cursor={page.NextCursor}");
            seen.AddRange(page.Items.Select(item => item.Id));
        }

        Assert.Equal(ids, seen);
    }

    [Fact]
    public async Task A_page_carries_epic_dependencies_and_claim_like_the_unpaged_list()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory");
        var blocker = await AddAsync(fixture, "blocker", item => item.CreatedAtUtc = Start);
        var blocked = await AddAsync(fixture, "blocked", item => item.CreatedAtUtc = Start.AddMinutes(1));
        var later = await AddAsync(fixture, "later", item => item.CreatedAtUtc = Start.AddMinutes(2));
        await fixture.Client.PostAsJsonAsync($"/api/items/{blocked}/dependencies", new AddWorkItemDependencyRequest(blocker));
        await fixture.Client.PostAsJsonAsync($"/api/items/{later}/dependencies", new AddWorkItemDependencyRequest(blocked));
        await fixture.ClaimAsync(blocked, caller, "run-1", 60);
        await AddCommentAsync(fixture, blocked, "a comment body");

        var first = await GetPageAsync(fixture, "/api/items?limit=1");
        var second = await GetPageAsync(fixture, $"/api/items?limit=1&cursor={first.NextCursor}");
        var unpaged = (await fixture.Client.GetFromJsonAsync<List<WorkItemDto>>("/api/items"))!.Single(item => item.Id == blocked);

        var paged = Assert.Single(second.Items);
        Assert.Equal(blocked, paged.Id);
        Assert.Equal(unpaged.Blockers, paged.Blockers);
        Assert.Equal(unpaged.Dependents, paged.Dependents);
        Assert.Equal(blocker, Assert.Single(paged.Blockers).Id);
        Assert.Equal(later, Assert.Single(paged.Dependents).Id);
        Assert.Equal("run-1", paged.ClaimMarker);
        Assert.Equal("a comment body", Assert.Single(paged.Comments).Body);
        Assert.Equal(blocked, Assert.Single(Assert.Single(first.Items).Dependents).Id);
    }

    [Theory]
    [InlineData("/api/items?includeComments=false")]
    [InlineData("/api/items?includeComments=false&limit=10")]
    public async Task With_comments_excluded_the_response_carries_no_comment_bodies(string path)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await AddAsync(fixture, "item");
        await AddCommentAsync(fixture, item, "SECRET-COMMENT-BODY");

        var excluded = await fixture.Client.GetStringAsync(path);
        var included = await fixture.Client.GetStringAsync(path.Replace("includeComments=false", "includeComments=true", StringComparison.Ordinal));

        Assert.DoesNotContain("SECRET-COMMENT-BODY", excluded, StringComparison.Ordinal);
        Assert.Contains("\"comments\":[]", excluded, StringComparison.Ordinal);
        Assert.Contains(item.ToString(), excluded, StringComparison.Ordinal);
        Assert.Contains("SECRET-COMMENT-BODY", included, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_new_arguments_the_response_is_the_array_it_always_was()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var second = await AddAsync(fixture, "second", item => { item.Order = 1; item.CreatedAtUtc = Start; });
        var first = await AddAsync(fixture, "first", item => { item.Order = 0; item.CreatedAtUtc = Start.AddMinutes(5); });
        var ready = await AddAsync(fixture, "ready", item => { item.Status = WorkItemStatus.Ready; item.CreatedAtUtc = Start.AddMinutes(1); });
        await AddAsync(fixture, "closed", item => item.Status = WorkItemStatus.Closed);
        await AddCommentAsync(fixture, first, "body");

        using var json = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/items"));

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        // Board order: status, then order within the column; closed items left out; comments included.
        Assert.Equal([first, second, ready], json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(
            [
                "id", "projectId", "epicId", "epic", "title", "description", "type", "status", "priority", "order", "estimate",
                "labels", "comments", "createdAtUtc", "updatedAtUtc", "claimant", "claimant_id", "claim_marker",
                "claim_expires_at_utc", "blockers", "dependents", "links", "version", "label_list"
            ],
            json.RootElement[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal("body", json.RootElement[0].GetProperty("comments")[0].GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=")]
    [InlineData("cursor=MTIzNDU")]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("updatedSince=yesterday")]
    [InlineData("updatedSince=")]
    [InlineData("updatedSince=10/12/2026")]
    [InlineData("claimState=mine")]
    [InlineData("claimState=1")]
    [InlineData("label=")]
    [InlineData("label=a,b")]
    public async Task A_malformed_argument_is_refused_invalid_argument(string query)
    {
        using var fixture = await ApiFixture.CreateAsync();
        await AddAsync(fixture, "item");

        var response = await fixture.Client.GetAsync($"/api/items?{query}");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
    }

    [Theory]
    [InlineData("2026-10-01T09:00:00Z", 0)]
    [InlineData("2026-10-01T11:00:00+02:00", 0)]
    [InlineData("2026-10-01T09:00:00", 0)]
    [InlineData("2026-10-01T09:00:00.1Z", 1000000)]
    [InlineData("2026-10-01T09:00:00.12", 1200000)]
    [InlineData("2026-10-01T09:00:00.123+00:00", 1230000)]
    [InlineData("2026-10-01T09:00:00.1234Z", 1234000)]
    [InlineData("2026-10-01T09:00:00.12345Z", 1234500)]
    [InlineData("2026-10-01T09:00:00.123456Z", 1234560)]
    // Seven digits are accepted and cut to the microsecond.
    [InlineData("2026-10-01T09:00:00.1234567+00:00", 1234560)]
    public void Updated_since_accepts_iso_8601_timestamps(string value, long ticksPastTheHour)
    {
        Assert.True(WorkItemListing.TryParseTimestamp(value, out var timestamp));

        Assert.Equal(Start.AddHours(1).AddTicks(ticksPastTheHour), timestamp);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
    }

    [Fact]
    public void Updated_since_accepts_a_date_alone_as_midnight_utc_and_the_stamps_the_api_writes()
    {
        Assert.True(WorkItemListing.TryParseTimestamp("2026-10-01", out var midnight));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), midnight);

        // updatedAtUtc exactly as the API serialises it, with and without a fraction.
        foreach (var stamp in new[] { Start, Start.AddTicks(1234560), Start.AddTicks(1200000) })
        {
            var written = JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(stamp))!;
            Assert.True(WorkItemListing.TryParseTimestamp(written, out var read), written);
            Assert.Equal(stamp, read);
        }
    }

    [Theory]
    [InlineData("10/12/2026")]
    [InlineData("12 Oct 2026")]
    [InlineData("2026-10-12 12:00:00Z")]
    [InlineData("2026-10-12T12:00:00.12345678Z")]
    [InlineData("12:00:00")]
    public void Updated_since_refuses_what_is_not_iso_8601(string value) =>
        Assert.False(WorkItemListing.TryParseTimestamp(value, out _));

    [Fact]
    public void A_cursor_round_trips_and_shows_nothing_readable()
    {
        var cursor = new WorkItemCursor(Start.AddTicks(1234567), Guid.NewGuid());

        var encoded = cursor.Encode(WorkItemCursor.WorkItems);

        Assert.True(WorkItemCursor.TryDecode(encoded, WorkItemCursor.WorkItems, out var decoded));
        // A cursor names its list: the comment list does not take it.
        Assert.False(WorkItemCursor.TryDecode(encoded, WorkItemCursor.Comments, out _));
        Assert.Equal(cursor, decoded);
        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);
        Assert.DoesNotContain(cursor.Id.ToString("N"), encoded, StringComparison.OrdinalIgnoreCase);
    }

    private static List<Guid> Sorted(params Guid[] ids) => ids.Order().ToList();

    private static Task<Guid> AddAsync(ApiFixture fixture, string title, Action<WorkItem>? configure = null) =>
        fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            var project = await dbContext.Projects.FirstOrDefaultAsync();
            if (project is null)
            {
                project = new Project { Name = "Project", Key = "PRJ" };
                dbContext.Projects.Add(project);
            }

            var item = new WorkItem { ProjectId = project.Id, Title = title };
            configure?.Invoke(item);
            dbContext.WorkItems.Add(item);
            await dbContext.SaveChangesAsync();
            return item.Id;
        });

    private static Task<int> AddCommentAsync(ApiFixture fixture, Guid itemId, string body) =>
        fixture.WithServicesAsync(services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            dbContext.WorkItemComments.Add(new WorkItemComment { WorkItemId = itemId, Author = "tester", Body = body });
            return dbContext.SaveChangesAsync();
        });

    private static async Task<List<Guid>> ListIdsAsync(ApiFixture fixture, string path)
    {
        using var response = await fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<List<WorkItemDto>>(body, JsonSerializerOptions.Web)!.Select(item => item.Id).Order().ToList();
    }

    private static async Task<WorkItemPageDto> GetPageAsync(ApiFixture fixture, string path)
    {
        using var response = await fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["items", "next_cursor"], json.RootElement.EnumerateObject().Select(property => property.Name));
        return JsonSerializer.Deserialize<WorkItemPageDto>(body, JsonSerializerOptions.Web)!;
    }

    // Follows next_cursor from the first page to the last.
    private static async Task<List<WorkItemPageDto>> WalkAsync(ApiFixture fixture, string path)
    {
        var pages = new List<WorkItemPageDto> { await GetPageAsync(fixture, path) };
        while (pages[^1].NextCursor is { } cursor)
        {
            Assert.True(pages.Count < 50, "The walk does not end.");
            pages.Add(await GetPageAsync(fixture, $"{path}&cursor={cursor}"));
        }

        return pages;
    }
}
