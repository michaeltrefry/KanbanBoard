using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Api.Services;

public static class KanbanSettingsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapKanbanSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/me")
            .WithTags("User Settings");

        group.MapGet("", async (
            ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var user = await GetCurrentUserOrNullAsync(currentUserService, cancellationToken);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(ToCurrentUserDto(user));
        })
        .WithName("GetCurrentUser");

        group.MapGet("/personal-access-tokens", async (
            ICurrentUserService currentUserService,
            KanbanDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var user = await GetCurrentUserOrNullAsync(currentUserService, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var tokens = await dbContext.PersonalAccessTokens
                .AsNoTracking()
                .Where(token => token.AppUserId == user.Id)
                .ToListAsync(cancellationToken);

            return Results.Ok(tokens
                .OrderByDescending(token => token.CreatedAtUtc)
                .ThenBy(token => token.Name)
                .Select(ToPersonalAccessTokenMetadataDto)
                .ToList());
        })
        .WithName("ListCurrentUserPersonalAccessTokens");

        group.MapPost("/personal-access-tokens", async (
            CreatePersonalAccessTokenRequest request,
            ICurrentUserService currentUserService,
            IPersonalAccessTokenService personalAccessTokenService,
            IOptions<PersonalAccessTokenOptions> personalAccessTokenOptions,
            CancellationToken cancellationToken) =>
        {
            if (!personalAccessTokenOptions.Value.Enabled)
            {
                return Results.Problem(
                    title: "Personal access tokens are disabled.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var validationError = ValidateCreatePersonalAccessTokenRequest(request);
            if (validationError is not null)
            {
                return Results.BadRequest(new { message = validationError });
            }

            var user = await GetCurrentUserOrNullAsync(currentUserService, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var created = await personalAccessTokenService.CreateAsync(
                user,
                request.Name,
                request.ExpiresAtUtc,
                cancellationToken);

            return Results.Created(
                $"/api/me/personal-access-tokens/{created.Token.Id}",
                new CreatedPersonalAccessTokenDto(
                    ToPersonalAccessTokenMetadataDto(created.Token),
                    created.PlaintextToken));
        })
        .WithName("CreateCurrentUserPersonalAccessToken");

        group.MapDelete("/personal-access-tokens/{tokenId:guid}", async (
            Guid tokenId,
            ICurrentUserService currentUserService,
            IPersonalAccessTokenService personalAccessTokenService,
            CancellationToken cancellationToken) =>
        {
            var user = await GetCurrentUserOrNullAsync(currentUserService, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var revoked = await personalAccessTokenService.RevokeAsync(user.Id, tokenId, cancellationToken);
            return revoked ? Results.NoContent() : Results.NotFound();
        })
        .WithName("RevokeCurrentUserPersonalAccessToken");

        return endpoints;
    }

    private static async Task<AppUser?> GetCurrentUserOrNullAsync(
        ICurrentUserService currentUserService,
        CancellationToken cancellationToken)
    {
        try
        {
            return await currentUserService.GetOrCreateCurrentUserAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static CurrentUserDto ToCurrentUserDto(AppUser user) =>
        new(
            user.Id,
            user.Issuer,
            user.Subject,
            user.DisplayName,
            user.Email,
            user.LastSeenAtUtc);

    private static PersonalAccessTokenMetadataDto ToPersonalAccessTokenMetadataDto(PersonalAccessToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return new PersonalAccessTokenMetadataDto(
            token.Id,
            token.Name,
            token.TokenPrefix,
            token.CreatedAtUtc,
            token.ExpiresAtUtc,
            token.LastUsedAtUtc,
            token.RevokedAtUtc,
            token.RevokedAtUtc is null && (token.ExpiresAtUtc is null || token.ExpiresAtUtc > now));
    }

    private static string? ValidateCreatePersonalAccessTokenRequest(CreatePersonalAccessTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Field 'name' is required.";
        }

        if (request.Name.Trim().Length > 160)
        {
            return "Field 'name' must be 160 characters or fewer.";
        }

        if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return "Field 'expiresAtUtc' must be in the future.";
        }

        return null;
    }
}
