using FeatureLab.Features.Chat;
using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Data;

public sealed class FeatureLabDbContext(DbContextOptions<FeatureLabDbContext> options)
    : IdentityDbContext<FeatureLabUser>(options)
{
    public DbSet<PersistedChatMessage> ChatMessages => Set<PersistedChatMessage>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var chatMessage = modelBuilder.Entity<PersistedChatMessage>();

        chatMessage.ToTable("ChatMessages");
        chatMessage.HasKey(message => message.Id);
        chatMessage.Property(message => message.AuthorId)
            .HasMaxLength(450)
            .IsRequired();
        chatMessage.Property(message => message.Sender)
            .HasMaxLength(80)
            .IsRequired();
        chatMessage.Property(message => message.Text)
            .HasMaxLength(ChatHub.MaximumMessageLength)
            .IsRequired();
        chatMessage.Property(message => message.SentAtUtc)
            .IsRequired();
        chatMessage.HasIndex(message => new
        {
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
        workItem.Property(item => item.Version)
            .IsConcurrencyToken()
            .IsRequired();
        workItem.HasIndex(item => new { item.OwnerId, item.CreatedAtUtc });
        workItem.HasIndex(item => item.CreatedAtUtc);
    }
}
