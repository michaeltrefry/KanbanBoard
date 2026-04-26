using KanbanBoard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KanbanBoard.Api.Data;

public sealed class KanbanDbContext(DbContextOptions<KanbanDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Epic> Epics => Set<Epic>();
    public DbSet<EpicDocument> EpicDocuments => Set<EpicDocument>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<WorkItemComment> WorkItemComments => Set<WorkItemComment>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(project => project.Name).HasMaxLength(160).IsRequired();
            entity.Property(project => project.Key).HasMaxLength(24).IsRequired();
            entity.Property(project => project.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(project => project.Key).IsUnique();
        });

        modelBuilder.Entity<Epic>(entity =>
        {
            entity.HasKey(epic => epic.Id);
            entity.Property(epic => epic.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(epic => epic.ProjectId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(epic => epic.Name).HasMaxLength(160).IsRequired();
            entity.Property(epic => epic.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(epic => epic.UpdatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(epic => new { epic.ProjectId, epic.Name });
            entity.HasOne(epic => epic.Project)
                .WithMany(project => project.Epics)
                .HasForeignKey(epic => epic.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EpicDocument>(entity =>
        {
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(document => document.EpicId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(document => document.Title).HasMaxLength(200).IsRequired();
            entity.Property(document => document.Body).IsRequired();
            entity.Property(document => document.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(document => document.UpdatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(document => new { document.EpicId, document.CreatedAtUtc });
            entity.HasOne(document => document.Epic)
                .WithMany(epic => epic.Documents)
                .HasForeignKey(document => document.EpicId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(item => item.ProjectId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(item => item.EpicId).HasConversion(NullableGuidToStringConverter).HasMaxLength(36);
            entity.Property(item => item.Title).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Labels).HasMaxLength(240);
            entity.Property(item => item.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(item => item.UpdatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(item => new { item.ProjectId, item.Status, item.Order });
            entity.HasIndex(item => item.EpicId);
            entity.HasOne(item => item.Project)
                .WithMany(project => project.Items)
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Epic)
                .WithMany(epic => epic.Items)
                .HasForeignKey(item => item.EpicId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkItemComment>(entity =>
        {
            entity.HasKey(comment => comment.Id);
            entity.Property(comment => comment.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(comment => comment.WorkItemId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(comment => comment.Author).HasMaxLength(80).IsRequired();
            entity.Property(comment => comment.Body).IsRequired();
            entity.Property(comment => comment.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(comment => new { comment.WorkItemId, comment.CreatedAtUtc });
            entity.HasOne(comment => comment.WorkItem)
                .WithMany(item => item.Comments)
                .HasForeignKey(comment => comment.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(user => user.Issuer).HasMaxLength(512).IsRequired();
            entity.Property(user => user.Subject).HasMaxLength(200).IsRequired();
            entity.Property(user => user.DisplayName).HasMaxLength(200);
            entity.Property(user => user.Email).HasMaxLength(320);
            entity.Property(user => user.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(user => user.UpdatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(user => user.LastSeenAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(user => new { user.Issuer, user.Subject }).IsUnique();
        });

        modelBuilder.Entity<PersonalAccessToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.Property(token => token.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(token => token.AppUserId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(token => token.Name).HasMaxLength(160).IsRequired();
            entity.Property(token => token.TokenPrefix).HasMaxLength(32).IsRequired();
            entity.Property(token => token.TokenHash).HasMaxLength(128).IsRequired();
            entity.Property(token => token.EncryptedSecret).HasMaxLength(1024).IsRequired();
            entity.Property(token => token.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(token => token.ExpiresAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(token => token.LastUsedAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(token => token.RevokedAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(token => token.AppUserId);
            entity.HasIndex(token => token.TokenPrefix);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasOne(token => token.AppUser)
                .WithMany(user => user.PersonalAccessTokens)
                .HasForeignKey(token => token.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static readonly ValueConverter<Guid, string> GuidToStringConverter = new(
        value => value.ToString(),
        value => Guid.Parse(value));

    private static readonly ValueConverter<Guid?, string?> NullableGuidToStringConverter = new(
        value => value.HasValue ? value.Value.ToString() : null,
        value => value != null ? Guid.Parse(value) : null);

    private static readonly ValueConverter<DateTimeOffset, DateTime> DateTimeOffsetToUtcDateTimeConverter = new(
        value => value.UtcDateTime,
        value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

    private static readonly ValueConverter<DateTimeOffset?, DateTime?> NullableDateTimeOffsetToUtcDateTimeConverter = new(
        value => value.HasValue ? value.Value.UtcDateTime : null,
        value => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null);
}
