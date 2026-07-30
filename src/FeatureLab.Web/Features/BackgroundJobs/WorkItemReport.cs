namespace FeatureLab.Features.BackgroundJobs;

public sealed class WorkItemReport
{
    private WorkItemReport()
    {
    }

    private WorkItemReport(
        Guid id,
        Guid workItemId,
        string ownerId,
        Guid tenantId,
        DateTime requestedAtUtc)
    {
        Id = id;
        WorkItemId = workItemId;
        OwnerId = ownerId;
        TenantId = tenantId;
        RequestedAtUtc = requestedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid WorkItemId { get; private set; }

    public string OwnerId { get; private set; } = string.Empty;

    public Guid TenantId { get; private set; }

    public DateTime RequestedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public string? Content { get; private set; }

    public static WorkItemReport Request(
        Guid workItemId,
        string ownerId,
        Guid tenantId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (workItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A work item identifier is required.",
                nameof(workItemId));
        }

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException(
                "An authenticated owner is required.",
                nameof(ownerId));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A tenant identifier is required.",
                nameof(tenantId));
        }

        return new WorkItemReport(
            Guid.NewGuid(),
            workItemId,
            ownerId,
            tenantId,
            timeProvider.GetUtcNow().UtcDateTime);
    }

    public void Complete(string content, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (CompletedAtUtc is not null)
        {
            return;
        }

        var normalizedContent = content?.Trim() ?? string.Empty;
        if (normalizedContent.Length is < 1 or > 500)
        {
            throw new ArgumentException(
                "Report content must contain between 1 and 500 characters.",
                nameof(content));
        }

        Content = normalizedContent;
        CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
    }
}
