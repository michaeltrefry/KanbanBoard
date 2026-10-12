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

// Runs the comment endpoints against SQLite on a file, like WorkItemLinkApiTests: real SQL, a real unique index,
// and one connection per request so concurrent adds genuinely race.
public sealed class WorkItemCommentApiTests
{
    [Fact]
    public async Task Add_with_a_token_returns_the_comment_id_and_stamps_the_token_identity_whatever_author_is_passed()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(3650));

        using var response = await AddAsync(fixture, item, factory, new { author = "someone else", body = " shipped " });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, raw);
        using var json = JsonDocument.Parse(raw);
        var dto = JsonSerializer.Deserialize<WorkItemDto>(raw, JsonSerializerOptions.Web)!;
        var comment = Assert.Single(dto.Comments);
        Assert.Equal(comment.Id, json.RootElement.GetProperty("comment_id").GetGuid());
        Assert.True(json.RootElement.GetProperty("comment_created").GetBoolean());
        Assert.NotEqual(Guid.Empty, comment.Id);
        Assert.Equal(("Factory", factory.User.Id, "shipped", fixture.Clock.Now), (comment.Author, comment.AuthorId, comment.Body, comment.CreatedAtUtc));
        Assert.Equal(fixture.Clock.Now, dto.UpdatedAtUtc);
        // The names a comment has always had, plus author_id.
        Assert.Equal(
            ["author", "author_id", "body", "createdAtUtc", "id", "workItemId"],
            json.RootElement.GetProperty("comments")[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        var stored = Assert.Single(await CommentsAsync(fixture));
        Assert.Equal((comment.Id, "Factory", factory.User.Id), (stored.Id, stored.Author, stored.AuthorUserId));
    }

    [Fact]
    public async Task Add_with_a_token_needs_no_author()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();

        var dto = await ReadAsync(await AddAsync(fixture, item, factory, new { body = "note" }));

        Assert.Equal("Factory", Assert.Single(dto.Comments).Author);
    }

    [Fact]
    public async Task Add_without_a_token_keeps_the_author_the_caller_names_and_has_no_author_id()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();

        var dto = await ReadAsync(await fixture.Client.PostAsJsonAsync($"/api/items/{item}/comments", new { author = " tester ", body = "note" }));
        using var missingAuthor = await fixture.Client.PostAsJsonAsync($"/api/items/{item}/comments", new { body = "note" });
        using var longAuthor = await fixture.Client.PostAsJsonAsync($"/api/items/{item}/comments", new { author = new string('a', 321), body = "note" });
        using var missingBody = await fixture.Client.PostAsJsonAsync($"/api/items/{item}/comments", new { author = "tester", body = " " });

        var comment = Assert.Single(dto.Comments);
        Assert.Equal(("tester", (Guid?)null), (comment.Author, comment.AuthorId));
        Assert.Equal(comment.Id, dto.CommentId);
        await AssertErrorAsync(missingAuthor, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(longAuthor, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        await AssertErrorAsync(missingBody, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Single(await CommentsAsync(fixture));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Add_with_a_revoked_or_expired_token_is_refused_scope_denied_and_never_falls_back_to_the_named_author(bool revoked)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var caller = await fixture.CreateCallerAsync("Factory", expiresAtUtc: revoked ? null : fixture.Clock.Now.AddMinutes(1));
        var item = await fixture.AddItemAsync();
        if (revoked)
        {
            await fixture.WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().PersonalAccessTokens
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAtUtc, fixture.Clock.Now)));
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));

        using var response = await AddAsync(fixture, item, caller, new { author = "spoofed", body = "note" });

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
        Assert.Empty(await CommentsAsync(fixture));
    }

    [Fact]
    public async Task Add_and_list_for_an_unknown_item_are_refused_not_found()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");

        using var added = await AddAsync(fixture, Guid.NewGuid(), factory, new { body = "note", idempotencyKey = "k" });
        using var listed = await fixture.Client.GetAsync($"/api/items/{Guid.NewGuid()}/comments");

        await AssertErrorAsync(added, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        await AssertErrorAsync(listed, HttpStatusCode.NotFound, KanbanErrorCodes.NotFound);
        Assert.Empty(await CommentsAsync(fixture));
    }

    [Fact]
    public async Task A_repeated_idempotency_key_returns_the_first_comment_and_changes_nothing()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();
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

        var first = await ReadAsync(await AddAsync(fixture, item, factory, new { body = "first body", idempotencyKey = "run-1:start" }));
        Assert.Equal(1, Published());
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        using var repeatResponse = await AddAsync(fixture, item, factory, new { body = "first body", idempotencyKey = "run-1:start" });
        // The key names the comment: a repeat with another body still reports the first one, unchanged.
        using var otherBodyResponse = await AddAsync(fixture, item, factory, new { body = "another body", idempotencyKey = "run-1:start" });

        Assert.Equal(HttpStatusCode.OK, repeatResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherBodyResponse.StatusCode);
        foreach (var repeat in new[] { await ReadAsync(repeatResponse), await ReadAsync(otherBodyResponse) })
        {
            Assert.Equal((first.CommentId, false), (repeat.CommentId, repeat.CommentCreated));
            Assert.Equal(first.UpdatedAtUtc, repeat.UpdatedAtUtc);
            Assert.Equal((first.CommentId, "first body"), (Assert.Single(repeat.Comments).Id, repeat.Comments[0].Body));
        }

        Assert.True(first.CommentCreated);
        Assert.Equal(0, Published());
        Assert.Equal(first.UpdatedAtUtc, (await fixture.GetItemAsync(item)).UpdatedAtUtc);
        Assert.Equal("first body", Assert.Single(await CommentsAsync(fixture)).Body);
    }

    [Fact]
    public async Task An_idempotency_key_belongs_to_one_identity_and_one_item_and_is_compared_exactly()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var other = await fixture.CreateCallerAsync("Other");
        var item = await fixture.AddItemAsync("A");
        var sibling = await fixture.AddItemAsync("B");

        var ids = new List<Guid?>
        {
            (await ReadAsync(await AddAsync(fixture, item, factory, new { body = "1", idempotencyKey = "key" }))).CommentId,
            (await ReadAsync(await AddAsync(fixture, item, other, new { body = "2", idempotencyKey = "key" }))).CommentId,
            (await ReadAsync(await AddAsync(fixture, sibling, factory, new { body = "3", idempotencyKey = "key" }))).CommentId,
            (await ReadAsync(await AddAsync(fixture, item, factory, new { body = "4", idempotencyKey = "KEY" }))).CommentId,
            (await ReadAsync(await fixture.Client.PostAsJsonAsync($"/api/items/{item}/comments", new { author = "ui", body = "5", idempotencyKey = "key" }))).CommentId,
            // Without a key every call is a new comment.
            (await ReadAsync(await AddAsync(fixture, item, factory, new { body = "6" }))).CommentId,
            (await ReadAsync(await AddAsync(fixture, item, factory, new { body = "6" }))).CommentId
        };
        // Callers without a token share one identity.
        var repeatedWithoutToken = await ReadAsync(await fixture.Client.PostAsJsonAsync($"/api/items/{item}/comments", new { author = "someone", body = "7", idempotencyKey = "key" }));

        Assert.Equal(7, ids.Distinct().Count());
        Assert.Equal((ids[4], false), (repeatedWithoutToken.CommentId, repeatedWithoutToken.CommentCreated));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "6"], (await CommentsAsync(fixture)).Select(comment => comment.Body).Order().ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData(" padded")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("café")]
    [InlineData("✓")]
    public async Task A_malformed_idempotency_key_is_refused_invalid_argument_and_writes_nothing(string key)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();
        var before = (await fixture.GetItemAsync(item)).UpdatedAtUtc;

        using var response = await AddAsync(fixture, item, factory, new { body = "note", idempotencyKey = key });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Empty(await CommentsAsync(fixture));
        Assert.Equal(before, (await fixture.GetItemAsync(item)).UpdatedAtUtc);
    }

    [Fact]
    public async Task Idempotency_key_length_is_capped_at_128_characters()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();
        var longest = new string('k', WorkItemCommentLimits.MaxIdempotencyKeyLength - 4) + "!~:/";

        using var tooLong = await AddAsync(fixture, item, factory, new { body = "note", idempotencyKey = longest + "k" });
        var added = await ReadAsync(await AddAsync(fixture, item, factory, new { body = "note", idempotencyKey = longest }));
        var repeated = await ReadAsync(await AddAsync(fixture, item, factory, new { body = "note", idempotencyKey = longest }));

        Assert.Equal(128, WorkItemCommentLimits.MaxIdempotencyKeyLength);
        await AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
        Assert.Equal(added.CommentId, repeated.CommentId);
        Assert.Single(await CommentsAsync(fixture));
    }

    [Fact]
    public async Task Concurrent_adds_with_one_idempotency_key_store_exactly_one_comment_and_all_report_it()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");

        for (var round = 0; round < 8; round++)
        {
            var item = await fixture.AddItemAsync($"R{round}");
            using var start = new SemaphoreSlim(0);
            var attempts = Enumerable.Range(0, 6)
                .Select(attempt => Task.Run(async () =>
                {
                    await start.WaitAsync();
                    return await AddAsync(fixture, item, factory, new { body = $"attempt {attempt}", idempotencyKey = "same" });
                }))
                .ToList();
            start.Release(attempts.Count);
            var responses = await Task.WhenAll(attempts);

            var results = new List<WorkItemDto>();
            foreach (var response in responses)
            {
                results.Add(await ReadAsync(response));
            }

            var stored = Assert.Single(await CommentsAsync(fixture, item));
            Assert.All(results, result => Assert.Equal(stored.Id, result.CommentId));
            Assert.Equal(1, results.Count(result => result.CommentCreated == true));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        }
    }

    [Fact]
    public async Task Comments_written_at_the_same_instant_get_increasing_times_and_the_item_carries_the_last_one()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(3650));

        var times = new List<DateTimeOffset>();
        WorkItemDto last = null!;
        for (var index = 0; index < 4; index++)
        {
            last = await ReadAsync(await AddAsync(fixture, item, factory, new { body = $"c{index}" }));
            times.Add(last.Comments.Single(comment => comment.Id == last.CommentId).CreatedAtUtc);
        }

        var microsecond = TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond);
        Assert.Equal(Enumerable.Range(0, 4).Select(index => fixture.Clock.Now + index * microsecond).ToArray(), times.ToArray());
        Assert.Equal(times[^1], last.UpdatedAtUtc);
        Assert.Equal(["c0", "c1", "c2", "c3"], last.Comments.Select(comment => comment.Body).ToArray());
    }

    [Fact]
    public async Task List_returns_id_author_timestamp_and_body_in_creation_order_for_the_item_only()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync("A");
        var sibling = await fixture.AddItemAsync("B");
        fixture.Clock.Advance(TimeSpan.FromDays(3650));
        var first = await ReadAsync(await AddAsync(fixture, item, factory, new { body = "first" }));
        await AddAsync(fixture, sibling, factory, new { body = "elsewhere" });
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await ReadAsync(await fixture.Client.PostAsJsonAsync($"/api/items/{item}/comments", new { author = "tester", body = "second" }));

        using var response = await fixture.Client.GetAsync($"/api/items/{item}/comments");

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(["items", "next_cursor"], json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("next_cursor").ValueKind);
        Assert.Equal(
            ["author", "author_id", "body", "createdAtUtc", "id", "workItemId"],
            json.RootElement.GetProperty("items")[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        var page = JsonSerializer.Deserialize<WorkItemCommentPageDto>(raw, JsonSerializerOptions.Web)!;
        Assert.Equal(
            [
                new WorkItemCommentDto(first.CommentId!.Value, item, "Factory", "first", fixture.Clock.Now.AddSeconds(-1), factory.User.Id),
                new WorkItemCommentDto(second.CommentId!.Value, item, "tester", "second", fixture.Clock.Now, null)
            ],
            page.Items.ToArray());
        // The item's own reads embed the same records.
        var embedded = (await fixture.Client.GetFromJsonAsync<WorkItemDto>($"/api/items/{item}"))!.Comments;
        Assert.Equal(page.Items.ToArray(), embedded.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Paging_returns_every_comment_exactly_once_also_when_several_share_one_timestamp(int limit)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();
        // Comments older than the rule that every comment is later than the one before it: three at one instant,
        // then two at another, then one.
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var seeded = await SeedAsync(fixture, item, [at, at, at, at.AddSeconds(1), at.AddSeconds(1), at.AddSeconds(2)]);

        var pages = await WalkAsync(fixture, $"/api/items/{item}/comments?limit={limit}");

        var walked = pages.SelectMany(page => page.Items).ToList();
        Assert.Equal(seeded.Order().ToArray(), walked.Select(comment => comment.Id).Order().ToArray());
        Assert.Equal(walked.Select(comment => comment.CreatedAtUtc).Order().ToArray(), walked.Select(comment => comment.CreatedAtUtc).ToArray());
        Assert.Equal((int)Math.Ceiling(6d / limit), pages.Count);
        Assert.All(pages.SkipLast(1), page => Assert.Equal(limit, page.Items.Count));
    }

    [Fact]
    public async Task A_comment_added_during_a_walk_is_returned_after_the_ones_already_read()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();
        // The stored comments are ahead of the writer's clock.
        var seeded = await SeedAsync(fixture, item, [fixture.Clock.Now.AddHours(1), fixture.Clock.Now.AddHours(1), fixture.Clock.Now.AddHours(1)]);

        var firstPage = await GetPageAsync(fixture, $"/api/items/{item}/comments?limit=3");
        var added = await ReadAsync(await AddAsync(fixture, item, factory, new { body = "late" }));
        var rest = await GetPageAsync(fixture, $"/api/items/{item}/comments?limit=3&cursor={firstPage.NextCursor ?? LastCursor(firstPage)}");

        Assert.Equal(seeded.Order().ToArray(), firstPage.Items.Select(comment => comment.Id).Order().ToArray());
        Assert.Equal(added.CommentId, Assert.Single(rest.Items).Id);
        Assert.Equal(added.UpdatedAtUtc, rest.Items[0].CreatedAtUtc);
        Assert.True(rest.Items[0].CreatedAtUtc > firstPage.Items[^1].CreatedAtUtc);
    }

    [Fact]
    public async Task List_pages_default_to_50_and_an_item_without_comments_gives_an_empty_page()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync("A");
        var empty = await fixture.AddItemAsync("B");
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedAsync(fixture, item, Enumerable.Range(0, 51).Select(index => at.AddSeconds(index)).ToArray());

        var first = await GetPageAsync(fixture, $"/api/items/{item}/comments");
        var second = await GetPageAsync(fixture, $"/api/items/{item}/comments?cursor={first.NextCursor}");
        var none = await GetPageAsync(fixture, $"/api/items/{empty}/comments");

        Assert.Equal((50, 1, null), (first.Items.Count, second.Items.Count, second.NextCursor));
        Assert.Equal((0, null), (none.Items.Count, none.NextCursor));
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("limit=-1")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=")]
    public async Task A_malformed_limit_or_cursor_is_refused_invalid_argument(string query)
    {
        using var fixture = await ApiFixture.CreateAsync();
        var item = await fixture.AddItemAsync();

        using var response = await fixture.Client.GetAsync($"/api/items/{item}/comments?{query}");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, KanbanErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task A_read_scope_token_can_list_comments_and_cannot_add_one()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var reader = await fixture.CreateCallerAsync("Reader", PersonalAccessTokenScopes.Read);
        var item = await fixture.AddItemAsync();
        await SeedAsync(fixture, item, [fixture.Clock.Now]);
        using var list = fixture.InternalRequest($"/api/items/{item}/comments?limit=200", reader.Token.Id, new { });
        list.Method = HttpMethod.Get;
        list.Content = null;

        using var listed = await fixture.Client.SendAsync(list);
        using var added = await AddAsync(fixture, item, reader, new { body = "note" });

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Single((await listed.Content.ReadFromJsonAsync<WorkItemCommentPageDto>())!.Items);
        await AssertErrorAsync(added, HttpStatusCode.Forbidden, KanbanErrorCodes.ScopeDenied);
    }

    [Fact]
    public async Task Deleting_an_item_deletes_its_comments_and_frees_their_idempotency_keys()
    {
        using var fixture = await ApiFixture.CreateAsync();
        var factory = await fixture.CreateCallerAsync("Factory");
        var item = await fixture.AddItemAsync();
        await AddAsync(fixture, item, factory, new { body = "note", idempotencyKey = "key" });

        await fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            dbContext.WorkItems.Remove(await dbContext.WorkItems.SingleAsync(candidate => candidate.Id == item));
            return await dbContext.SaveChangesAsync();
        });

        Assert.Empty(await CommentsAsync(fixture));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("run-42:step/3_done.v2~!", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a\u007f", false)]
    public void Idempotency_keys_are_printable_ascii_without_spaces(string key, bool valid) =>
        Assert.Equal(valid, WorkItemComments.IsValidIdempotencyKey(key));

    [Fact]
    public void The_idempotency_key_hash_covers_the_identity_and_the_exact_key()
    {
        var user = Guid.NewGuid();
        var hash = WorkItemComments.HashIdempotencyKey(user, "key");

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, WorkItemComments.HashIdempotencyKey(user, "key"));
        Assert.NotEqual(hash, WorkItemComments.HashIdempotencyKey(user, "KEY"));
        Assert.NotEqual(hash, WorkItemComments.HashIdempotencyKey(Guid.NewGuid(), "key"));
        Assert.NotEqual(hash, WorkItemComments.HashIdempotencyKey(null, "key"));
    }

    private static Task<HttpResponseMessage> AddAsync(ApiFixture fixture, Guid item, PersonalAccessTokenValidationResult caller, object body) =>
        fixture.PostAsync($"/api/items/{item}/comments", caller, body);

    private static async Task<WorkItemDto> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
            return JsonSerializer.Deserialize<WorkItemDto>(body, JsonSerializerOptions.Web)!;
        }
    }

    private static Task<List<WorkItemComment>> CommentsAsync(ApiFixture fixture, Guid? item = null) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<KanbanDbContext>().WorkItemComments
            .AsNoTracking()
            .Where(comment => item == null || comment.WorkItemId == item)
            .ToListAsync());

    // Comments as they were written before this story: no author id, no key, and whatever time the writer had.
    private static Task<List<Guid>> SeedAsync(ApiFixture fixture, Guid item, DateTimeOffset[] times) =>
        fixture.WithServicesAsync(async services =>
        {
            var dbContext = services.GetRequiredService<KanbanDbContext>();
            var comments = times
                .Select((time, index) => new WorkItemComment { WorkItemId = item, Author = "tester", Body = $"seeded {index}", CreatedAtUtc = time })
                .ToList();
            dbContext.WorkItemComments.AddRange(comments);
            await dbContext.SaveChangesAsync();
            return comments.Select(comment => comment.Id).ToList();
        });

    private static string LastCursor(WorkItemCommentPageDto page) =>
        new WorkItemCursor(page.Items[^1].CreatedAtUtc, page.Items[^1].Id).Encode();

    private static async Task<WorkItemCommentPageDto> GetPageAsync(ApiFixture fixture, string path)
    {
        using var response = await fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<WorkItemCommentPageDto>(body, JsonSerializerOptions.Web)!;
    }

    // Follows next_cursor from the first page to the last.
    private static async Task<List<WorkItemCommentPageDto>> WalkAsync(ApiFixture fixture, string path)
    {
        var pages = new List<WorkItemCommentPageDto> { await GetPageAsync(fixture, path) };
        while (pages[^1].NextCursor is { } cursor)
        {
            Assert.True(pages.Count < 50, "The walk does not end.");
            pages.Add(await GetPageAsync(fixture, $"{path}&cursor={cursor}"));
        }

        return pages;
    }
}
