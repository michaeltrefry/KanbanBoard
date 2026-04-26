using System.Net;
using System.Net.Http.Json;
using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Mcp.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Tests;

public sealed class McpPersonalAccessTokenAuthenticationTests
{
    [Fact]
    public async Task Validator_returns_null_without_calling_api_for_missing_token()
    {
        using var services = CreateServices(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var validator = services.GetRequiredService<IMcpPersonalAccessTokenValidator>();

        var result = await validator.ValidateAsync("", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, services.GetRequiredService<CapturingHandler>().CallCount);
    }

    [Fact]
    public async Task Validator_returns_null_for_invalid_pat_response()
    {
        using var services = CreateServices(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var validator = services.GetRequiredService<IMcpPersonalAccessTokenValidator>();

        var result = await validator.ValidateAsync("kbp_prefix_secret", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, services.GetRequiredService<CapturingHandler>().CallCount);
    }

    [Fact]
    public async Task Validator_returns_validated_token_and_caches_successful_response()
    {
        var appUserId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();
        using var services = CreateServices(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ValidatedPersonalAccessTokenDto(
                    appUserId,
                    tokenId,
                    "Ada",
                    "ada@example.test",
                    DateTimeOffset.UtcNow.AddMinutes(10)))
            };
            return response;
        });
        var validator = services.GetRequiredService<IMcpPersonalAccessTokenValidator>();

        var first = await validator.ValidateAsync("kbp_prefix_secret", CancellationToken.None);
        var second = await validator.ValidateAsync("kbp_prefix_secret", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(appUserId, first.AppUserId);
        Assert.Equal(tokenId, first.PersonalAccessTokenId);
        Assert.Equal("Ada", first.DisplayName);
        Assert.NotNull(second);
        Assert.Equal(1, services.GetRequiredService<CapturingHandler>().CallCount);
    }

    [Fact]
    public async Task Internal_api_handler_adds_shared_secret_and_validated_pat_context()
    {
        var secret = new string('i', 32);
        var appUserId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();
        using var services = CreateServices(
            request => new HttpResponseMessage(HttpStatusCode.OK),
            secret,
            new McpAuthenticatedToken(appUserId, tokenId, "Ada", "ada@example.test", null));
        var clientFactory = services.GetRequiredService<IHttpClientFactory>();

        var response = await clientFactory.CreateClient("kanban-api").GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(services.GetRequiredService<CapturingHandler>().Requests);
        Assert.Equal(secret, Assert.Single(request.Headers.GetValues(KanbanInternalAuth.SecretHeaderName)));
        Assert.Equal(appUserId.ToString(), Assert.Single(request.Headers.GetValues(KanbanInternalAuth.AppUserIdHeaderName)));
        Assert.Equal(tokenId.ToString(), Assert.Single(request.Headers.GetValues(KanbanInternalAuth.PersonalAccessTokenIdHeaderName)));
    }

    private static ServiceProvider CreateServices(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? internalSecret = null,
        McpAuthenticatedToken? authenticatedToken = null)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticatedToken is not null)
        {
            httpContext.Items[typeof(McpAuthenticatedToken)] = authenticatedToken;
        }

        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton(Options.Create(new McpAuthenticationOptions
        {
            RequirePersonalAccessToken = true,
            ValidationCacheSeconds = 30
        }));
        services.AddSingleton(Options.Create(new InternalApiOptions { SharedSecret = internalSecret }));
        services.AddSingleton(new CapturingHandler(respond));
        services.AddTransient<InternalApiAuthenticationHandler>();
        services.AddSingleton<IMcpPersonalAccessTokenValidator, McpPersonalAccessTokenValidator>();
        services
            .AddHttpClient("kanban-api", client => client.BaseAddress = new Uri("https://kanban.example.test"))
            .AddHttpMessageHandler<InternalApiAuthenticationHandler>()
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<CapturingHandler>());

        return services.BuildServiceProvider();
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Requests.Add(request);
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
