using FeatureLab.Features.BackgroundJobs;
using FeatureLab.Features.Chat;
using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Data;

public sealed class FeatureLabDbContext(
    DbContextOptions<FeatureLabDbContext> options,
    ITenantContext tenantContext)
    : IdentityDbContext<FeatureLabUser>(options)
{
    private Guid CurrentTenantId => tenantContext.Id;

    public DbSet<PersistedChatMessage> ChatMessages => Set<PersistedChatMessage>();

    public DbSet<WorkItemReport> WorkItemReports => Set<WorkItemReport>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var user = modelBuilder.Entity<FeatureLabUser>();

        user.Property(member => member.TenantId)
            .IsRequired();
        user.HasIndex(member => member.TenantId);

        var chatMessage = modelBuilder.Entity<PersistedChatMessage>();

        chatMessage.ToTable("ChatMessages");
        chatMessage.HasKey(message => message.Id);
        chatMessage.Property(message => message.AuthorId)
            .HasMaxLength(450)
            .IsRequired();
        chatMessage.Property(message => message.TenantId)
            .IsRequired();
        chatMessage.Property(message => message.Sender)
            .HasMaxLength(80)
            .IsRequired();
        chatMessage.Property(message => message.Text)
            .HasMaxLength(ChatHub.MaximumMessageLength)
            .IsRequired();
        chatMessage.Property(message => message.SentAtUtc)
            .IsRequired();
        chatMessage.HasQueryFilter(message =>
            message.TenantId == CurrentTenantId);
        chatMessage.HasIndex(message => new
        {
            message.TenantId,
            message.SentAtUtc,
            message.Id,
        });

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
        workItem.Property(item => item.TenantId)
            .IsRequired();
        workItem.Property(item => item.Version)
            .IsConcurrencyToken()
            .IsRequired();
        workItem.HasQueryFilter(item =>
            item.TenantId == CurrentTenantId);
        workItem.HasIndex(item => new
        {
            item.TenantId,
            item.OwnerId,
            item.CreatedAtUtc,
        });
        workItem.HasIndex(item => item.CreatedAtUtc);
        workItem.HasAlternateKey(item => new
        {
            item.TenantId,
            item.Id,
        });

        var workItemReport = modelBuilder.Entity<WorkItemReport>();

        workItemReport.ToTable("WorkItemReports");
        workItemReport.HasKey(report => report.Id);
        workItemReport.Property(report => report.OwnerId)
            .HasMaxLength(450)
            .IsRequired();
        workItemReport.Property(report => report.TenantId)
            .IsRequired();
        workItemReport.Property(report => report.RequestedAtUtc)
            .IsRequired();
        workItemReport.Property(report => report.Content)
            .HasMaxLength(500);
        workItemReport.HasQueryFilter(report =>
            report.TenantId == CurrentTenantId);
        workItemReport.HasIndex(report => new
        {
            report.TenantId,
            report.OwnerId,
            report.RequestedAtUtc,
        });
        workItemReport.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(report => new
            {
                report.TenantId,
                report.WorkItemId,
            })
            .HasPrincipalKey(item => new
            {
                item.TenantId,
                item.Id,
            })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
