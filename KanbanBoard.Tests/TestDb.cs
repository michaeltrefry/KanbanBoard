using KanbanBoard.Api.Configuration;
using KanbanBoard.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KanbanBoard.Tests;

internal static class TestDb
{
    public static KanbanDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<KanbanDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new KanbanDbContext(options);
    }

    public static IOptions<PersonalAccessTokenOptions> PatOptions(string? encryptionKey = null) =>
        Options.Create(new PersonalAccessTokenOptions
        {
            Enabled = true,
            EncryptionKey = encryptionKey ?? Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            TokenPrefix = "kbp"
        });
}
