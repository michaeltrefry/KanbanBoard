using KanbanBoard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanBoard.Api.Data;

public sealed class KanbanDbContext(DbContextOptions<KanbanDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Epic> Epics => Set<Epic>();
    public DbSet<EpicDocument> EpicDocuments => Set<EpicDocument>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Name).HasMaxLength(160).IsRequired();
            entity.Property(project => project.Key).HasMaxLength(24).IsRequired();
            entity.HasIndex(project => project.Key).IsUnique();
        });

        modelBuilder.Entity<Epic>(entity =>
        {
            entity.HasKey(epic => epic.Id);
            entity.Property(epic => epic.Name).HasMaxLength(160).IsRequired();
            entity.HasIndex(epic => new { epic.ProjectId, epic.Name });
            entity.HasOne(epic => epic.Project)
                .WithMany(project => project.Epics)
                .HasForeignKey(epic => epic.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EpicDocument>(entity =>
        {
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Title).HasMaxLength(200).IsRequired();
            entity.Property(document => document.Body).IsRequired();
            entity.HasIndex(document => new { document.EpicId, document.CreatedAtUtc });
            entity.HasOne(document => document.Epic)
                .WithMany(epic => epic.Documents)
                .HasForeignKey(document => document.EpicId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Labels).HasMaxLength(240);
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
    }
}
