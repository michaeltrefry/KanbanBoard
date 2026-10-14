using System.Net;

namespace KanbanBoard.Tests;

// The story page lives at /stories/{storyId}: the real application serves the app's HTML there so a link to a story
// opens in the browser, and leaves every other path, the API's and the static files', as it was.
public sealed class StoryPageFallbackTests
{
    [Fact]
    public async Task A_story_path_returns_the_app_html()
    {
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        using var client = api.CreateClient();

        using var response = await client.GetAsync($"/stories/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\"></div>", body);
        // Absolute, so they load from under /stories/ too.
        Assert.Contains("src=\"/app.js", body);
        Assert.Contains("href=\"/styles.css", body);
    }

    [Fact]
    public async Task A_story_path_that_is_not_a_story_id_is_not_found()
    {
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        using var client = api.CreateClient();

        using var notAnId = await client.GetAsync("/stories/not-an-id");
        using var nested = await client.GetAsync($"/stories/{Guid.NewGuid()}/more");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (notAnId.StatusCode, nested.StatusCode));
    }

    [Fact]
    public async Task Only_the_hyphenated_id_form_returns_the_app_html()
    {
        // The app reads only the hyphenated form from the path; the other forms a Guid parses from would load the
        // app on a path it does not recognise as a story.
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        using var client = api.CreateClient();
        var id = Guid.NewGuid();

        using var upperCase = await client.GetAsync($"/stories/{id.ToString("D").ToUpperInvariant()}");
        using var digitsOnly = await client.GetAsync($"/stories/{id:N}");
        using var braced = await client.GetAsync($"/stories/{id:B}");

        Assert.Equal(
            (HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.NotFound),
            (upperCase.StatusCode, digitsOnly.StatusCode, braced.StatusCode));
    }

    [Fact]
    public async Task An_unknown_api_path_is_still_a_404_without_html()
    {
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        using var client = api.CreateClient();

        using var unknown = await client.GetAsync("/api/does-not-exist");
        using var underStories = await client.GetAsync($"/api/stories/{Guid.NewGuid()}");

        foreach (var response in new[] { unknown, underStories })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("<div id=\"root\">", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Static_files_are_still_served_as_themselves()
    {
        using var api = new ProgramDependencyWiringTests.ApiApplication();
        using var client = api.CreateClient();

        using var styles = await client.GetAsync("/styles.css");
        using var script = await client.GetAsync("/app.js");
        using var root = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, styles.StatusCode);
        Assert.Equal("text/css", styles.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("function App()", await script.Content.ReadAsStringAsync());
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
    }
}
