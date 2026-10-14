using KanbanBoard.Shared.Contracts;
using KanbanBoard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KanbanBoard.Api.Data;

public sealed class KanbanDbContext(DbContextOptions<KanbanDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Epic> Epics => Set<Epic>();
    public DbSet<EpicDocument> EpicDocuments => Set<EpicDocument>();
    public DbSet<EpicPlanImport> EpicPlanImports => Set<EpicPlanImport>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<WorkItemComment> WorkItemComments => Set<WorkItemComment>();
    public DbSet<WorkItemDependency> WorkItemDependencies => Set<WorkItemDependency>();
    public DbSet<WorkItemLink> WorkItemLinks => Set<WorkItemLink>();
    public DbSet<WorkItemChange> WorkItemChanges => Set<WorkItemChange>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(project => project.Name).HasMaxLength(ProjectLimits.MaxNameLength).IsRequired();
            entity.Property(project => project.Key).HasMaxLength(ProjectLimits.MaxKeyLength).IsRequired();
            entity.Property(project => project.RepositoryUrl).HasMaxLength(ProjectLimits.MaxRepositoryUrlLength);
            entity.Property(project => project.DefaultBranch).HasMaxLength(ProjectLimits.MaxDefaultBranchLength);
            entity.Property(project => project.IdempotencyKeyHash).HasMaxLength(64);
            entity.Property(project => project.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(project => project.Key).IsUnique();
            // One project per idempotency key (the hash covers the caller's identity). Projects created without a
            // key hold null here, and a unique index admits any number of nulls.
            entity.HasIndex(project => project.IdempotencyKeyHash).IsUnique();
        });

        modelBuilder.Entity<Epic>(entity =>
        {
            entity.HasKey(epic => epic.Id);
            entity.Property(epic => epic.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(epic => epic.ProjectId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(epic => epic.Name).HasMaxLength(EpicLimits.MaxNameLength).IsRequired();
            entity.Property(epic => epic.IdempotencyKeyHash).HasMaxLength(64);
            entity.Property(epic => epic.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(epic => epic.UpdatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(epic => new { epic.ProjectId, epic.Name });
            // One epic per project and idempotency key (the hash covers the caller's identity). Epics created
            // without a key hold null here, and a unique index admits any number of nulls.
            entity.HasIndex(epic => new { epic.ProjectId, epic.IdempotencyKeyHash }).IsUnique();
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
            entity.Property(document => document.Title).HasMaxLength(EpicLimits.MaxDocumentTitleLength).IsRequired();
            entity.Property(document => document.Body).IsRequired();
            entity.Property(document => document.IdempotencyKeyHash).HasMaxLength(64);
            entity.Property(document => document.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(document => document.UpdatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(document => new { document.EpicId, document.CreatedAtUtc });
            // One document per epic and idempotency key; documents created without a key hold null.
            entity.HasIndex(document => new { document.EpicId, document.IdempotencyKeyHash }).IsUnique();
            entity.HasOne(document => document.Epic)
                .WithMany(epic => epic.Documents)
                .HasForeignKey(document => document.EpicId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EpicPlanImport>(entity =>
        {
            entity.HasKey(import => import.EpicId);
            entity.Property(import => import.EpicId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(import => import.ProjectId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(import => import.IdempotencyKeyHash).HasMaxLength(64).IsRequired();
            entity.Property(import => import.Result).IsRequired();
            // One import per project and idempotency key (the hash covers the caller's identity).
            entity.HasIndex(import => new { import.ProjectId, import.IdempotencyKeyHash }).IsUnique();
            // The record goes with its epic, and with it the key: a repeat after the epic was deleted imports again.
            entity.HasOne(import => import.Epic)
                .WithOne()
                .HasForeignKey<EpicPlanImport>(import => import.EpicId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(item => item.ProjectId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(item => item.EpicId).HasConversion(NullableGuidToStringConverter).HasMaxLength(36);
            entity.Property(item => item.Title).HasMaxLength(WorkItemLimits.MaxTitleLength).IsRequired();
            entity.Property(item => item.Labels).HasMaxLength(WorkItemLimits.MaxLabelsLength);
            // Items that are older than the column start at 1, like a new item.
            entity.Property(item => item.Version).HasDefaultValue(1L);
            entity.Property(item => item.IdempotencyKeyHash).HasMaxLength(64);
            entity.Property(item => item.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(item => item.UpdatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(item => item.ClaimantUserId).HasConversion(NullableGuidToStringConverter).HasMaxLength(36);
            entity.Property(item => item.ClaimantName).HasMaxLength(WorkItemClaimLimits.MaxClaimantNameLength);
            var claimMarker = entity.Property(item => item.ClaimMarker).HasMaxLength(WorkItemClaimLimits.MaxMarkerLength);
            if (Database.ProviderName == "MySql.EntityFrameworkCore")
            {
                // Markers are opaque ids: compare them byte for byte, not under the database's case-insensitive default.
                claimMarker.UseCollation("utf8mb4_bin");
            }

            entity.Property(item => item.ClaimExpiresAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(item => new { item.ProjectId, item.Status, item.Order });
            entity.HasIndex(item => item.EpicId);
            // One item per project and idempotency key; items created without a key hold null.
            entity.HasIndex(item => new { item.ProjectId, item.IdempotencyKeyHash }).IsUnique();
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
            entity.Property(comment => comment.Author).HasMaxLength(WorkItemCommentLimits.MaxAuthorLength).IsRequired();
            entity.Property(comment => comment.AuthorUserId).HasConversion(NullableGuidToStringConverter).HasMaxLength(36);
            entity.Property(comment => comment.Body).IsRequired();
            entity.Property(comment => comment.IdempotencyKeyHash).HasMaxLength(64);
            entity.Property(comment => comment.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.HasIndex(comment => new { comment.WorkItemId, comment.CreatedAtUtc });
            // One comment per item and idempotency key (the hash covers the caller's identity). Comments without a
            // key hold null here, and a unique index admits any number of nulls.
            entity.HasIndex(comment => new { comment.WorkItemId, comment.IdempotencyKeyHash }).IsUnique();
            entity.HasOne(comment => comment.WorkItem)
                .WithMany(item => item.Comments)
                .HasForeignKey(comment => comment.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemDependency>(entity =>
        {
            entity.HasKey(dependency => new { dependency.BlockedItemId, dependency.BlockerItemId });
            entity.Property(dependency => dependency.BlockedItemId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(dependency => dependency.BlockerItemId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.HasIndex(dependency => dependency.BlockerItemId);
            // Both sides cascade, so deleting an item (or its project) removes every edge that names it.
            entity.HasOne(dependency => dependency.BlockedItem)
                .WithMany()
                .HasForeignKey(dependency => dependency.BlockedItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(dependency => dependency.BlockerItem)
                .WithMany()
                .HasForeignKey(dependency => dependency.BlockerItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemLink>(entity =>
        {
            entity.HasKey(link => link.Id);
            entity.Property(link => link.Id).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(link => link.WorkItemId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(link => link.Kind).HasMaxLength(WorkItemLinkLimits.MaxKindLength).IsRequired();
            entity.Property(link => link.Url).HasMaxLength(WorkItemLinkLimits.MaxUrlLength).IsRequired();
            entity.Property(link => link.UrlHash).HasMaxLength(64).IsRequired();
            entity.Property(link => link.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            // One link per item, kind and URL. The same URL may be attached to several items.
            entity.HasIndex(link => new { link.WorkItemId, link.Kind, link.UrlHash }).IsUnique();
            entity.HasIndex(link => link.UrlHash);
            entity.HasOne(link => link.WorkItem)
                .WithMany()
                .HasForeignKey(link => link.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemChange>(entity =>
        {
            entity.HasKey(change => change.Sequence);
            entity.Property(change => change.Sequence).ValueGeneratedOnAdd();
            // No foreign keys: an entry stays readable after its item or its project is deleted.
            entity.Property(change => change.WorkItemId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(change => change.ProjectId).HasConversion(GuidToStringConverter).HasMaxLength(36);
            entity.Property(change => change.Kind).HasMaxLength(32).IsRequired();
            entity.Property(change => change.ActorUserId).HasConversion(NullableGuidToStringConverter).HasMaxLength(36);
            entity.Property(change => change.ActorName).HasMaxLength(WorkItemClaimLimits.MaxClaimantNameLength);
            entity.Property(change => change.Changes).IsRequired();
            entity.Property(change => change.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            // The feed of one project, in feed order.
            entity.HasIndex(change => new { change.ProjectId, change.Sequence });
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
            entity.Property(user => user.DisabledAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
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
            entity.Property(token => token.Scope).HasMaxLength(16).IsRequired().HasDefaultValue(PersonalAccessTokenScopes.Full);
            entity.Property(token => token.CreatedAtUtc).HasConversion(DateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(token => token.ExpiresAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(token => token.LastUsedAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(token => token.RevokedAtUtc).HasConversion(NullableDateTimeOffsetToUtcDateTimeConverter).HasColumnType("datetime(6)");
            entity.Property(token => token.IssuedByUserId).HasConversion(NullableGuidToStringConverter).HasMaxLength(36);
            entity.Property(token => token.IssuedByName).HasMaxLength(WorkItemClaimLimits.MaxClaimantNameLength);
            entity.Property(token => token.RevokedByUserId).HasConversion(NullableGuidToStringConverter).HasMaxLength(36);
            entity.Property(token => token.RevokedByName).HasMaxLength(WorkItemClaimLimits.MaxClaimantNameLength);
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
