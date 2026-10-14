using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using KanbanBoard.Api.Models;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Tests;

public sealed class ServiceIdentityTests
{
    [Fact]
    public async Task Command_creates_named_identity_with_a_full_and_a_read_token_that_validate_as_it()
    {
        await using var services = CreateServices();
        var output = new StringWriter();

        var exitCode = await ServiceIdentityCommand.RunAsync(
            [ServiceIdentityCommand.CommandName, "--name", "Dark Factory"], services, output, TextWriter.Null, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var dbContext = services.GetRequiredService<KanbanDbContext>();
        var user = await dbContext.AppUsers.SingleAsync();
        Assert.Equal(ServiceIdentityProvisioner.Issuer, user.Issuer);
        Assert.Equal("dark factory", user.Subject);
        Assert.Equal("Dark Factory", user.DisplayName);

        var patService = services.GetRequiredService<IPersonalAccessTokenService>();
        var full = await patService.ValidateAsync(PrintedToken(output, "full-scope token: "), CancellationToken.None);
        var read = await patService.ValidateAsync(PrintedToken(output, "read-scope token: "), CancellationToken.None);
        Assert.Equal((user.Id, PersonalAccessTokenScopes.Full), (full!.User.Id, full.Token.Scope));
        Assert.Equal((user.Id, PersonalAccessTokenScopes.Read), (read!.User.Id, read.Token.Scope));
        Assert.Equal(2, await dbContext.PersonalAccessTokens.CountAsync());
    }

    [Fact]
    public async Task Running_again_reuses_the_identity_and_keeps_old_tokens_unless_rotate_is_passed()
    {
        await using var services = CreateServices();
        var dbContext = services.GetRequiredService<KanbanDbContext>();
        var patService = services.GetRequiredService<IPersonalAccessTokenService>();

        var first = await ServiceIdentityProvisioner.ProvisionAsync(dbContext, patService, "factory", rotate: false, CancellationToken.None);
        var second = await ServiceIdentityProvisioner.ProvisionAsync(dbContext, patService, " Factory ", rotate: false, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.User.Id, second.User.Id);
        Assert.Equal("Factory", (await dbContext.AppUsers.SingleAsync()).DisplayName);
        Assert.NotNull(await patService.ValidateAsync(first.FullToken.PlaintextToken, CancellationToken.None));

        var rotated = await ServiceIdentityProvisioner.ProvisionAsync(dbContext, patService, "factory", rotate: true, CancellationToken.None);

        Assert.Equal(4, rotated.RevokedTokens);
        Assert.Null(await patService.ValidateAsync(first.FullToken.PlaintextToken, CancellationToken.None));
        Assert.Null(await patService.ValidateAsync(second.ReadToken.PlaintextToken, CancellationToken.None));
        Assert.NotNull(await patService.ValidateAsync(rotated.FullToken.PlaintextToken, CancellationToken.None));
        Assert.NotNull(await patService.ValidateAsync(rotated.ReadToken.PlaintextToken, CancellationToken.None));
    }

    [Theory]
    [InlineData]
    [InlineData("--name")]
    [InlineData("--name", "   ")]
    [InlineData("--name", "factory", "--unknown")]
    public async Task Command_with_bad_arguments_prints_usage_and_creates_nothing(params string[] arguments)
    {
        await using var services = CreateServices();
        var error = new StringWriter();

        var exitCode = await ServiceIdentityCommand.RunAsync(
            [ServiceIdentityCommand.CommandName, .. arguments], services, TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(await services.GetRequiredService<KanbanDbContext>().AppUsers.ToListAsync());
    }

    [Fact]
    public async Task Command_fails_when_personal_access_tokens_are_disabled()
    {
        await using var services = CreateServices(patEnabled: false);
        var error = new StringWriter();

        var exitCode = await ServiceIdentityCommand.RunAsync(
            [ServiceIdentityCommand.CommandName, "--name", "factory"], services, TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("not enabled", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(await services.GetRequiredService<KanbanDbContext>().AppUsers.ToListAsync());
    }

    [Fact]
    public async Task Rotate_with_personal_access_tokens_disabled_revokes_nothing()
    {
        await using var services = CreateServices();
        var dbContext = services.GetRequiredService<KanbanDbContext>();
        var patOptions = services.GetRequiredService<IOptions<PersonalAccessTokenOptions>>();
        Assert.Equal(0, await ServiceIdentityCommand.RunAsync(
            [ServiceIdentityCommand.CommandName, "--name", "factory"], services, TextWriter.Null, TextWriter.Null, CancellationToken.None));
        var error = new StringWriter();

        patOptions.Value.Enabled = false;
        var exitCode = await ServiceIdentityCommand.RunAsync(
            [ServiceIdentityCommand.CommandName, "--name", "factory", "--rotate"], services, TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("not enabled", error.ToString(), StringComparison.Ordinal);
        var tokens = await dbContext.PersonalAccessTokens.AsNoTracking().ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.Null(token.RevokedAtUtc));
    }

    // A disabled identity is enabled again only from the admin page; the command never issues a token for it.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Command_refuses_a_disabled_identity_and_writes_nothing(bool relational, bool rotate)
    {
        await using var services = CreateServices(relational: relational);
        var dbContext = services.GetRequiredService<KanbanDbContext>();
        var patService = services.GetRequiredService<IPersonalAccessTokenService>();
        var first = await ServiceIdentityProvisioner.ProvisionAsync(dbContext, patService, "Factory", rotate: false, CancellationToken.None);
        first.User.DisabledAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var error = new StringWriter();
        var output = new StringWriter();

        var exitCode = await ServiceIdentityCommand.RunAsync(
            [ServiceIdentityCommand.CommandName, "--name", "FACTORY", .. rotate ? new[] { "--rotate" } : []], services, output, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(ServiceIdentityProvisioner.DisabledMessage("Factory") + Environment.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        var tokens = await dbContext.PersonalAccessTokens.AsNoTracking().ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.Null(token.RevokedAtUtc));
        Assert.Equal("Factory", (await dbContext.AppUsers.AsNoTracking().SingleAsync()).DisplayName);
    }

    // The in-memory provider has no transactions, so there the order of the writes alone must protect the old tokens.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rotate_keeps_the_existing_tokens_when_issuing_the_new_pair_fails(bool relational)
    {
        await using var services = CreateServices(relational: relational);
        var dbContext = services.GetRequiredService<KanbanDbContext>();
        var patService = services.GetRequiredService<IPersonalAccessTokenService>();
        var first = await ServiceIdentityProvisioner.ProvisionAsync(dbContext, patService, "factory", rotate: false, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceIdentityProvisioner.ProvisionAsync(
            dbContext, new FailingOnSecondCreate(patService), "factory", rotate: true, CancellationToken.None));

        Assert.NotNull(await patService.ValidateAsync(first.FullToken.PlaintextToken, CancellationToken.None));
        Assert.NotNull(await patService.ValidateAsync(first.ReadToken.PlaintextToken, CancellationToken.None));
        if (relational)
        {
            // The full token issued before the failure was rolled back: no live token exists that was never printed.
            Assert.Equal(2, await dbContext.PersonalAccessTokens.CountAsync());
        }
    }

    private sealed class FailingOnSecondCreate(IPersonalAccessTokenService inner) : IPersonalAccessTokenService
    {
        private int creates;

        public Task<CreatedPersonalAccessToken> CreateAsync(
            AppUser user, string name, DateTimeOffset? expiresAtUtc, string scope, CancellationToken cancellationToken, ChangeActor? issuedBy = null) =>
            ++creates == 2
                ? throw new InvalidOperationException("The second token could not be issued.")
                : inner.CreateAsync(user, name, expiresAtUtc, scope, cancellationToken, issuedBy);

        public Task<PersonalAccessTokenValidationResult?> ValidateAsync(string token, CancellationToken cancellationToken) =>
            inner.ValidateAsync(token, cancellationToken);

        public Task<bool> RevokeAsync(Guid appUserId, Guid tokenId, CancellationToken cancellationToken) =>
            inner.RevokeAsync(appUserId, tokenId, cancellationToken);

        public Task<string?> GetScopeAsync(Guid tokenId, CancellationToken cancellationToken) =>
            inner.GetScopeAsync(tokenId, cancellationToken);

        public void EnsureEnabled() => inner.EnsureEnabled();
    }

    [Theory]
    [InlineData("create-service-identity", true)]
    [InlineData("--urls", false)]
    public void Only_the_command_name_as_first_argument_selects_the_command(string firstArgument, bool expected)
    {
        Assert.Equal(expected, ServiceIdentityCommand.IsCommand([firstArgument, "--name", "factory"]));
        Assert.False(ServiceIdentityCommand.IsCommand([]));
    }

    private static string PrintedToken(StringWriter output, string label) =>
        output.ToString().Split('\n').Single(line => line.StartsWith(label, StringComparison.Ordinal))[label.Length..].Trim();

    private static ServiceProvider CreateServices(bool patEnabled = true, bool relational = false)
    {
        var databaseName = Guid.NewGuid().ToString();
        var patOptions = TestDb.PatOptions();
        patOptions.Value.Enabled = patEnabled;
        var services = new ServiceCollection();
        if (relational)
        {
            // A private in-memory SQLite database that lives as long as this connection, which the provider disposes.
            services.AddSingleton(_ =>
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                return connection;
            });
            services.AddDbContext<KanbanDbContext>((provider, options) => options.UseSqlite(provider.GetRequiredService<SqliteConnection>()));
        }
        else
        {
            services.AddDbContext<KanbanDbContext>(options => options.UseInMemoryDatabase(databaseName));
        }

        services.AddSingleton(patOptions);
        services.AddScoped<IPersonalAccessTokenService, PersonalAccessTokenService>();
        var provider = services.BuildServiceProvider();
        if (relational)
        {
            provider.GetRequiredService<KanbanDbContext>().Database.EnsureCreated();
        }

        return provider;
    }
}
