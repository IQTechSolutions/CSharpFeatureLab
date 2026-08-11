namespace FeatureLab.Tenancy;

public sealed class TenantInvitation
{
    private TenantInvitation()
    {
    }

    private TenantInvitation(
        Guid id,
        Guid tenantId,
        string normalizedEmail,
        string codeHash,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty invitation identifier is required.",
                nameof(id));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty tenant identifier is required.",
                nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            throw new ArgumentException(
                "A normalized email address is required.",
                nameof(normalizedEmail));
        }

        if (string.IsNullOrWhiteSpace(codeHash))
        {
            throw new ArgumentException(
                "An invitation code hash is required.",
                nameof(codeHash));
        }

        Id = id;
        TenantId = tenantId;
        NormalizedEmail = normalizedEmail;
        CodeHash = codeHash;
        ExpiresAt = expiresAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public string? AcceptedByUserId { get; private set; }

    public long Version { get; private set; }

    public static TenantInvitation Create(
        Guid tenantId,
        string normalizedEmail,
        string codeHash,
        DateTimeOffset expiresAt) =>
        new(
            Guid.NewGuid(),
            tenantId,
            normalizedEmail,
            codeHash,
            expiresAt);

    public void Accept(
        string userId,
        DateTimeOffset acceptedAt)
    {
        if (AcceptedAt is not null)
        {
            throw new InvalidOperationException(
                "The tenant invitation has already been accepted.");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "A user identifier is required.",
                nameof(userId));
        }

        if (Version == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The tenant invitation version cannot advance further.");
        }

        AcceptedByUserId = userId;
        AcceptedAt = acceptedAt;
        Version++;
    }
}
