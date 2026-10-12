using KanbanBoard.Api.Data;
using KanbanBoard.Api.Services;
using KanbanBoard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    private static ServiceProvider CreateServices(bool patEnabled = true)
    {
        var databaseName = Guid.NewGuid().ToString();
        var patOptions = TestDb.PatOptions();
        patOptions.Value.Enabled = patEnabled;
        var services = new ServiceCollection();
        services.AddDbContext<KanbanDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(patOptions);
        services.AddScoped<IPersonalAccessTokenService, PersonalAccessTokenService>();
        return services.BuildServiceProvider();
    }
}
