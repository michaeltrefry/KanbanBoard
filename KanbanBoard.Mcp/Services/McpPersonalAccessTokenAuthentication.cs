using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using KanbanBoard.Mcp.Configuration;
using KanbanBoard.Shared.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Mcp.Services;

public sealed record McpAuthenticatedToken(
    Guid AppUserId,
    Guid PersonalAccessTokenId,
    string? DisplayName,
    string? Email,
    DateTimeOffset? ExpiresAtUtc);

public interface IMcpPersonalAccessTokenValidator
{
    Task<McpAuthenticatedToken?> ValidateAsync(string token, CancellationToken cancellationToken);
}

public sealed class McpPersonalAccessTokenValidator(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IOptions<McpAuthenticationOptions> mcpAuthenticationOptions) : IMcpPersonalAccessTokenValidator
{
    private readonly McpAuthenticationOptions mcpAuthenticationOptions = mcpAuthenticationOptions.Value;

    public async Task<McpAuthenticatedToken?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var cacheKey = $"pat:{HashToken(token)}";
        if (cache.TryGetValue<McpAuthenticatedToken>(cacheKey, out var cachedToken))
        {
            return cachedToken;
        }

        var client = httpClientFactory.CreateClient("kanban-api");
        var response = await client.PostAsJsonAsync(
            "/api/internal/personal-access-tokens/validate",
            new ValidatePersonalAccessTokenRequest(token),
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var validatedToken = await response.Content.ReadFromJsonAsync<ValidatedPersonalAccessTokenDto>(cancellationToken);
        if (validatedToken is null)
        {
            return null;
        }

        var authenticatedToken = new McpAuthenticatedToken(
            validatedToken.AppUserId,
            validatedToken.PersonalAccessTokenId,
            validatedToken.DisplayName,
            validatedToken.Email,
            validatedToken.ExpiresAtUtc);

        var cacheDuration = TimeSpan.FromSeconds(mcpAuthenticationOptions.ValidationCacheSeconds);
        if (validatedToken.ExpiresAtUtc is { } expiresAtUtc)
        {
            var remaining = expiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            cacheDuration = remaining < cacheDuration ? remaining : cacheDuration;
        }

        cache.Set(cacheKey, authenticatedToken, cacheDuration);
        return authenticatedToken;
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}

public sealed class InternalApiAuthenticationHandler(
    IHttpContextAccessor httpContextAccessor,
    IOptions<InternalApiOptions> internalApiOptions) : DelegatingHandler
{
    private readonly InternalApiOptions internalApiOptions = internalApiOptions.Value;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (internalApiOptions.HasUsableSharedSecret())
        {
            request.Headers.TryAddWithoutValidation(
                KanbanInternalAuth.SecretHeaderName,
                internalApiOptions.SharedSecret!.Trim());
        }

        if (httpContextAccessor.HttpContext?.Items[typeof(McpAuthenticatedToken)] is McpAuthenticatedToken token)
        {
            request.Headers.TryAddWithoutValidation(
                KanbanInternalAuth.AppUserIdHeaderName,
                token.AppUserId.ToString());
            request.Headers.TryAddWithoutValidation(
                KanbanInternalAuth.PersonalAccessTokenIdHeaderName,
                token.PersonalAccessTokenId.ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
