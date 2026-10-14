using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace KanbanBoard.Tests;

public sealed class AuthenticationMiddlewareTests
{
    [Fact]
    public async Task Auth_gate_rejects_anonymous_api_requests()
    {
        using var host = await CreateHostAsync(app =>
        {
            app.UseKanbanAuthenticationGate(new KanbanAuthOptions { Enabled = true });
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        });

        var response = await host.GetTestClient().GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_gate_redirects_anonymous_non_page_requests_to_login()
    {
        using var host = await CreateGateHostAsync();

        var response = await host.GetTestClient().GetAsync("/styles.css?v=2");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/login?returnUrl=%2Fstyles.css%3Fv%3D2", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("Accept", "text/html,application/xhtml+xml,*/*;q=0.8")]
    [InlineData("Sec-Fetch-Mode", "navigate")]
    public async Task Auth_gate_answers_anonymous_page_navigations_with_a_hash_preserving_sign_in_page(
        string header,
        string value)
    {
        using var host = await CreateGateHostAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/stories/{StoryId}");
        request.Headers.TryAddWithoutValidation(header, value);

        var response = await host.GetTestClient().SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var loginUrl = $"/auth/login?returnUrl=%2Fstories%2F{StoryId}";
        // The script appends the fragment, encoded, to returnUrl; the no-JS fallbacks go to the plain returnUrl.
        Assert.Contains(
            $"location.replace(\"{JavaScriptEncoder.Default.Encode(loginUrl)}\" + encodeURIComponent(location.hash));",
            html);
        Assert.Contains($"<noscript><meta http-equiv=\"refresh\" content=\"0;url={HtmlEncoder.Default.Encode(loginUrl)}\"></noscript>", html);
        Assert.Contains($"<a href=\"{HtmlEncoder.Default.Encode(loginUrl)}\">", html);
    }

    [Fact]
    public async Task Auth_gate_sign_in_page_escapes_the_return_url()
    {
        using var host = await CreateGateHostAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept", "text/html");

        var response = await client.GetAsync("/x%22%3C/script%3E?q=%3Cscript%3Ealert('x')%3C/script%3E&b=%22%3E%3Cimg");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = html[html.IndexOf("<title>", StringComparison.Ordinal)..];
        Assert.DoesNotContain("alert('x')", body);
        Assert.DoesNotContain("<img", body);
        Assert.DoesNotContain("</script>\"", body);
        Assert.Equal(1, CountOccurrences(html, "<script>"));
        Assert.Equal(1, CountOccurrences(html, "</script>"));
        Assert.Contains("returnUrl=%2Fx%2522%253C%2Fscript%253E%3Fq%3D%253Cscript%253Ealert", html);
    }

    [Fact]
    public async Task Auth_gate_still_rejects_anonymous_api_navigations()
    {
        using var host = await CreateGateHostAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept", "text/html");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");

        var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/stories/" + StoryId + "#comment-" + CommentId, "/stories/" + StoryId + "#comment-" + CommentId)]
    [InlineData("/stories/" + StoryId + "?tab=1#comment-" + CommentId, "/stories/" + StoryId + "?tab=1#comment-" + CommentId)]
    [InlineData("/stories/" + StoryId, "/stories/" + StoryId)]
    [InlineData("//evil.example#comment-1", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/\\evil.example#x", "/")]
    [InlineData("https://evil.example/stories/1#comment-1", "/")]
    [InlineData("/\t/evil.example", "/")]
    [InlineData("/\r\n/evil.example", "/")]
    [InlineData("/\n/evil.example", "/")]
    [InlineData("/stories/1\t#comment-1", "/")]
    [InlineData("evil.example", "/")]
    [InlineData("#comment-1", "/")]
    [InlineData("", "/")]
    public async Task Login_challenges_oidc_with_only_local_return_urls_fragment_included(
        string returnUrl,
        string expectedRedirectUri)
    {
        await AssertLoginRedirectUriAsync(Uri.EscapeDataString(returnUrl), expectedRedirectUri);
    }

    [Theory]
    [InlineData("/%09/evil.example", "/")]
    [InlineData("%2F%09%2Fevil.example", "/")]
    [InlineData("/%0D%0A/evil.example", "/")]
    [InlineData("/%0a/evil.example", "/")]
    [InlineData("/%2509/evil.example", "/%09/evil.example")]
    public async Task Login_rejects_percent_encoded_control_characters_in_return_url(
        string rawReturnUrl,
        string expectedRedirectUri)
    {
        await AssertLoginRedirectUriAsync(rawReturnUrl, expectedRedirectUri);
    }

    private static async Task AssertLoginRedirectUriAsync(string rawReturnUrlQueryValue, string expectedRedirectUri)
    {
        using var host = await CreateLoginHostAsync();

        var response = await host.GetTestClient().GetAsync($"/auth/login?returnUrl={rawReturnUrlQueryValue}");

        // The real OIDC handler sends the browser to the identity provider; the RedirectUri it returns to after the
        // callback rides in the protected `state` parameter.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(AuthorizationEndpoint, location.OriginalString);
        var state = QueryParameter(location, "state");
        var options = host.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        var properties = options.StateDataFormat.Unprotect(state);
        Assert.NotNull(properties);
        Assert.Equal(expectedRedirectUri, properties.RedirectUri);
    }

    [Fact]
    public async Task Auth_gate_allows_authenticated_api_requests()
    {
        using var host = await CreateHostAsync(app =>
        {
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                    "test"));
                return next(context);
            });
            app.UseKanbanAuthenticationGate(new KanbanAuthOptions { Enabled = true });
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        });

        var response = await host.GetTestClient().GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Antiforgery_protection_rejects_browser_mutations_without_token()
    {
        var antiforgery = new ThrowingAntiforgery();
        using var host = await CreateHostAsync(
            app =>
            {
                app.UseKanbanAntiforgeryProtection(new KanbanAuthOptions { Enabled = true });
                app.Run(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                });
            },
            services => services.AddSingleton<IAntiforgery>(antiforgery));

        var response = await host.GetTestClient().PostAsync("/api/projects", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, antiforgery.ValidateCallCount);
    }

    [Fact]
    public async Task Antiforgery_protection_skips_internal_service_requests()
    {
        var antiforgery = new ThrowingAntiforgery();
        var secret = new string('s', 32);
        using var host = await CreateHostAsync(
            app =>
            {
                app.UseKanbanInternalApiAuthentication(new InternalApiOptions { SharedSecret = secret });
                app.UseKanbanAntiforgeryProtection(new KanbanAuthOptions { Enabled = true });
                app.Run(context =>
                {
                    Assert.True(KanbanInternalApiExtensions.IsInternalApiRequest(context));
                    Assert.Equal("KanbanInternal", context.User.Identity?.AuthenticationType);
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                });
            },
            services => services.AddSingleton<IAntiforgery>(antiforgery));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects");
        request.Headers.TryAddWithoutValidation(KanbanInternalAuth.SecretHeaderName, secret);

        var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, antiforgery.ValidateCallCount);
    }

    private const string StoryId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string CommentId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string AuthorizationEndpoint = "https://idp.example/authorize";

    private static Task<IHost> CreateGateHostAsync() =>
        CreateHostAsync(app =>
        {
            app.UseKanbanAuthenticationGate(new KanbanAuthOptions { Enabled = true });
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        });

    private static Task<IHost> CreateLoginHostAsync() =>
        CreateHostAsync(
            app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseEndpoints(endpoints => endpoints.MapKanbanAuthenticationEndpoints());
            },
            services =>
            {
                services.AddScoped<ICurrentUserService, CurrentUserService>();
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                    })
                    .AddCookie()
                    .AddOpenIdConnect(options =>
                    {
                        options.ClientId = "kanban-test";
                        options.ResponseType = "code";
                        options.Configuration = new OpenIdConnectConfiguration
                        {
                            AuthorizationEndpoint = AuthorizationEndpoint,
                            Issuer = "https://idp.example"
                        };
                    });
            });

    private static string QueryParameter(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && Uri.UnescapeDataString(pair[..separator]) == name)
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new InvalidOperationException($"No '{name}' query parameter in {uri}.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<IHost> CreateHostAsync(
        Action<IApplicationBuilder> configure,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    configureServices?.Invoke(services);
                });
                webBuilder.Configure(configure);
            })
            .StartAsync();

        return host;
    }

    private sealed class ThrowingAntiforgery : IAntiforgery
    {
        public int ValidateCallCount { get; private set; }

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(false);

        public void SetCookieTokenAndHeader(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            ValidateCallCount++;
            throw new AntiforgeryValidationException("Missing test CSRF token.");
        }
    }
}
