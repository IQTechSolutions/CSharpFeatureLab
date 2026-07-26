namespace FeatureLab.Features.WorkItems;

public sealed class WorkItem
{
    private WorkItem()
    {
    }

    private WorkItem(Guid id, string title, string ownerId, DateTime createdAtUtc)
    {
        Id = id;
        Title = title;
        OwnerId = ownerId;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public bool IsCompleted { get; private set; }

    public string OwnerId { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public int Version { get; private set; }

    public static WorkItem Create(string title, string ownerId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("An authenticated owner is required.", nameof(ownerId));
        }

        return new WorkItem(
            Guid.NewGuid(),
            NormalizeTitle(title),
            ownerId,
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
