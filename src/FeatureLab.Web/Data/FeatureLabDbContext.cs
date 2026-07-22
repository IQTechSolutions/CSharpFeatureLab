using FeatureLab.Features.WorkItems;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Data;

public sealed class FeatureLabDbContext(DbContextOptions<FeatureLabDbContext> options)
    : DbContext(options)
{
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var workItem = modelBuilder.Entity<WorkItem>();

        workItem.ToTable("WorkItems");
        workItem.HasKey(item => item.Id);
        workItem.Property(item => item.Title)
            .HasMaxLength(120)
            .IsRequired();
        workItem.Property(item => item.CreatedAtUtc)
            .IsRequired();
        workItem.HasIndex(item => item.CreatedAtUtc);
    }
}

