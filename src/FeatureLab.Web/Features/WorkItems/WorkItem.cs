namespace FeatureLab.Features.WorkItems;

public sealed class WorkItem
{
    private WorkItem(Guid id, string title, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Title = title;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public string Title { get; }

    public bool IsCompleted { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static WorkItem Create(string title, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var normalizedTitle = title?.Trim() ?? string.Empty;
        if (normalizedTitle.Length is < 3 or > 120)
        {
            throw new ArgumentException("Title must contain between 3 and 120 characters.", nameof(title));
        }

        return new WorkItem(Guid.NewGuid(), normalizedTitle, timeProvider.GetUtcNow());
    }
}

