namespace FeatureLab.Tenancy;

public sealed class TenantMembershipRecord
{
    private TenantMembershipRecord()
    {
    }

    private TenantMembershipRecord(
        string userId,
        Guid tenantId,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "A user identifier is required.",
                nameof(userId));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty tenant identifier is required.",
                nameof(tenantId));
        }

        UserId = userId;
        TenantId = tenantId;
        Version = 1;
        IsActive = true;
        CreatedAt = createdAt;
    }

    public string UserId { get; private set; } = string.Empty;

    public Guid TenantId { get; private set; }

    public long Version { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public static TenantMembershipRecord Create(
        string userId,
        Guid tenantId,
        DateTimeOffset createdAt) =>
        new(userId, tenantId, createdAt);

    public void Reactivate()
    {
        AdvanceVersion();
        IsActive = true;
        RemovedAt = null;
    }

    public void Remove(DateTimeOffset removedAt)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException(
                "Only an active tenant membership can be removed.");
        }

        AdvanceVersion();
        IsActive = false;
        RemovedAt = removedAt;
    }

    private void AdvanceVersion()
    {
        if (Version == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The tenant membership version cannot advance further.");
        }

        Version++;
    }
}
