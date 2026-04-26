using System.Net;
using System.Security.Claims;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    public async Task Auth_gate_redirects_anonymous_static_requests_to_login()
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
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept", "text/html");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/login?returnUrl=%2F", response.Headers.Location?.OriginalString);
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
