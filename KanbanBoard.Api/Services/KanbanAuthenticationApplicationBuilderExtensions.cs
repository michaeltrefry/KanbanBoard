using KanbanBoard.Api.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using System.Security.Claims;

namespace KanbanBoard.Api.Services;

public static class KanbanAuthenticationApplicationBuilderExtensions
{
    public static IApplicationBuilder UseKanbanAuthenticationGate(
        this IApplicationBuilder app,
        KanbanAuthOptions authOptions)
    {
        if (!authOptions.Enabled)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            if (IsPublicAuthenticationPath(context.Request.Path, authOptions) ||
                context.User.Identity?.IsAuthenticated == true)
            {
                await next(context);
                return;
            }

            if (IsApiPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        });
    }

    public static IEndpointRouteBuilder MapKanbanAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/login", (string? returnUrl) =>
        {
            var redirectUri = NormalizeLocalReturnUrl(returnUrl) ?? "/";
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = redirectUri },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        })
        .AllowAnonymous()
        .WithName("Login");

        endpoints.MapMethods("/auth/logout", ["GET", "POST"], () =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous()
            .WithName("Logout");

        endpoints.MapGet("/api/auth/session", async (
            ClaimsPrincipal principal,
            ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            if (!currentUserService.TryGetExternalIdentity(principal, out var externalIdentity))
            {
                return Results.Unauthorized();
            }

            var user = await currentUserService.GetOrCreateCurrentUserAsync(cancellationToken);
            return Results.Ok(new
            {
                user.Id,
                externalIdentity.Issuer,
                externalIdentity.Subject,
                user.DisplayName,
                user.Email,
                user.LastSeenAtUtc
            });
        })
        .WithName("GetCurrentSession");

        return endpoints;
    }

    public static IApplicationBuilder UseKanbanAntiforgeryProtection(
        this IApplicationBuilder app,
        KanbanAuthOptions authOptions)
    {
        if (!authOptions.Enabled)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            if (!RequiresBrowserAntiforgery(context))
            {
                await next(context);
                return;
            }

            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();

            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { message = "A valid CSRF token is required." });
                return;
            }

            await next(context);
        });
    }

    public static IEndpointRouteBuilder MapKanbanAntiforgeryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        })
        .WithName("GetAntiforgeryToken");

        return endpoints;
    }

    private static bool IsPublicAuthenticationPath(PathString path, KanbanAuthOptions authOptions)
    {
        return path.StartsWithSegments("/auth/login", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/auth/logout", StringComparison.OrdinalIgnoreCase) ||
            MatchesConfiguredPath(path, authOptions.CallbackPath) ||
            MatchesConfiguredPath(path, authOptions.SignedOutCallbackPath);
    }

    private static bool IsApiPath(PathString path)
    {
        return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresBrowserAntiforgery(HttpContext context)
    {
        if (KanbanInternalApiExtensions.IsInternalApiRequest(context))
        {
            return false;
        }

        var request = context.Request;
        if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!HttpMethods.IsPost(request.Method) &&
            !HttpMethods.IsPut(request.Method) &&
            !HttpMethods.IsPatch(request.Method) &&
            !HttpMethods.IsDelete(request.Method))
        {
            return false;
        }

        return !request.Headers.ContainsKey("Authorization");
    }

    private static bool MatchesConfiguredPath(PathString requestPath, string configuredPath)
    {
        return PathString.FromUriComponent(configuredPath).Equals(requestPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return null;
        }

        return returnUrl.StartsWith("/", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : null;
    }
}
