using KanbanBoard.Api.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace KanbanBoard.Api.Services;

public static class KanbanAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddKanbanAuthentication(
        this IServiceCollection services,
        KanbanAuthOptions authOptions,
        IWebHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "KanbanBoard.Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = authOptions.Enabled && !environment.IsDevelopment()
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
        });

        if (!authOptions.Enabled)
        {
            return services;
        }

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = authOptions.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = authOptions.Authority;
                options.ClientId = authOptions.ClientId;
                options.ClientSecret = authOptions.ClientSecret;
                options.CallbackPath = authOptions.CallbackPath;
                options.SignedOutCallbackPath = authOptions.SignedOutCallbackPath;
                options.ResponseType = "code";
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "role";

                if (!string.IsNullOrWhiteSpace(authOptions.Audience))
                {
                    options.TokenValidationParameters.ValidAudience = authOptions.Audience;
                }

                options.Scope.Clear();
                foreach (var scope in authOptions.Scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)).Distinct(StringComparer.Ordinal))
                {
                    options.Scope.Add(scope);
                }
            });

        services.AddAuthorization();

        return services;
    }
}
