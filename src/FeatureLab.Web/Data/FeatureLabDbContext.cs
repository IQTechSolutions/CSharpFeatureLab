using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Data;

public sealed class FeatureLabDbContext(DbContextOptions<FeatureLabDbContext> options)
    : IdentityDbContext<FeatureLabUser>(options)
{
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var workItem = modelBuilder.Entity<WorkItem>();

        workItem.ToTable("WorkItems");
        workItem.HasKey(item => item.Id);
        workItem.Property(item => item.Title)
            .HasMaxLength(120)
            .IsRequired();
        workItem.Property(item => item.CreatedAtUtc)
            .IsRequired();
        workItem.Property(item => item.OwnerId)
            .HasMaxLength(450)
            .IsRequired();
        workItem.HasIndex(item => new { item.OwnerId, item.CreatedAtUtc });
        workItem.HasIndex(item => item.CreatedAtUtc);
    }
}
