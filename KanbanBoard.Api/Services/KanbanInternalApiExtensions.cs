using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using KanbanBoard.Api.Configuration;
using KanbanBoard.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Api.Services;

public static class KanbanInternalApiExtensions
{
    public static IApplicationBuilder UseKanbanInternalApiAuthentication(
        this IApplicationBuilder app,
        InternalApiOptions internalApiOptions)
    {
        return app.Use(async (context, next) =>
        {
            if (HasValidInternalSecret(context.Request, internalApiOptions))
            {
                context.Items[KanbanInternalAuth.InternalRequestItemKey] = true;

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, "kanban-internal-service"),
                    new(ClaimTypes.Name, "Kanban internal service")
                };

                if (TryGetSingleHeaderValue(context.Request, KanbanInternalAuth.AppUserIdHeaderName, out var appUserId))
                {
                    claims.Add(new Claim(KanbanInternalAuth.AppUserIdHeaderName, appUserId));
                }

                if (TryGetSingleHeaderValue(context.Request, KanbanInternalAuth.PersonalAccessTokenIdHeaderName, out var tokenId))
                {
                    claims.Add(new Claim(KanbanInternalAuth.PersonalAccessTokenIdHeaderName, tokenId));
                }

                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, KanbanInternalAuth.AuthenticationType));
            }

            await next(context);
        });
    }

    public static IEndpointRouteBuilder MapKanbanInternalApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/internal/personal-access-tokens/validate", async (
            HttpContext httpContext,
            ValidatePersonalAccessTokenRequest request,
            IPersonalAccessTokenService personalAccessTokenService,
            IOptions<PersonalAccessTokenOptions> personalAccessTokenOptions,
            CancellationToken cancellationToken) =>
        {
            if (httpContext.Items[KanbanInternalAuth.InternalRequestItemKey] is not true)
            {
                return Results.Unauthorized();
            }

            if (!personalAccessTokenOptions.Value.Enabled)
            {
                return Results.Problem(
                    title: "Personal access tokens are disabled.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.BadRequest(new { message = "Field 'token' is required." });
            }

            var result = await personalAccessTokenService.ValidateAsync(request.Token, cancellationToken);
            return result is null
                ? Results.Unauthorized()
                : Results.Ok(new ValidatedPersonalAccessTokenDto(
                    result.User.Id,
                    result.Token.Id,
                    result.User.DisplayName,
                    result.User.Email,
                    result.Token.ExpiresAtUtc));
        })
        .WithName("ValidateInternalPersonalAccessToken")
        .WithTags("Internal");

        return endpoints;
    }

    public static bool IsInternalApiRequest(HttpContext context) =>
        context.Items[KanbanInternalAuth.InternalRequestItemKey] is true;

    private static bool HasValidInternalSecret(HttpRequest request, InternalApiOptions internalApiOptions)
    {
        if (!internalApiOptions.HasUsableSharedSecret() ||
            !TryGetSingleHeaderValue(request, KanbanInternalAuth.SecretHeaderName, out var providedSecret))
        {
            return false;
        }

        return FixedTimeEquals(providedSecret, internalApiOptions.SharedSecret!.Trim());
    }

    private static bool TryGetSingleHeaderValue(HttpRequest request, string headerName, out string value)
    {
        value = string.Empty;
        if (!request.Headers.TryGetValue(headerName, out var headerValues) || headerValues.Count != 1)
        {
            return false;
        }

        value = headerValues[0] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
