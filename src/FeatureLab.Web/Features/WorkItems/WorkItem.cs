namespace FeatureLab.Features.WorkItems;

public sealed class WorkItem
{
    private WorkItem()
    {
    }

    private WorkItem(
        Guid id,
        string title,
        string ownerId,
        Guid tenantId,
        DateTime createdAtUtc)
    {
        Id = id;
        Title = title;
        OwnerId = ownerId;
        TenantId = tenantId;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public bool IsCompleted { get; private set; }

    public string OwnerId { get; private set; } = string.Empty;

    public Guid TenantId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public int Version { get; private set; }

    public static WorkItem Create(
        string title,
        string ownerId,
        Guid tenantId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("An authenticated owner is required.", nameof(ownerId));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A tenant identifier is required.",
                nameof(tenantId));
        }

        return new WorkItem(
            Guid.NewGuid(),
            NormalizeTitle(title),
            ownerId,
            tenantId,
            timeProvider.GetUtcNow().UtcDateTime);
    }

    public void Rename(string title)
    {
        Title = NormalizeTitle(title);
        Version = checked(Version + 1);
    }

    private static string NormalizeTitle(string title)
    {
        var normalizedTitle = title?.Trim() ?? string.Empty;
        if (normalizedTitle.Length is < 3 or > 120)
        {
            throw new ArgumentException(
                "Title must contain between 3 and 120 characters.",
                nameof(title));
        }

        return normalizedTitle;
    }
}
