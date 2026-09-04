using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace FeatureLab.Tenancy;

public interface ITenantInvitationDelivery
{
    Task DeliverAsync(
        Guid invitationId,
        string recipientEmail,
        string code,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}

public sealed class RecordedTenantInvitationDelivery
{
    internal RecordedTenantInvitationDelivery(
        Guid invitationId,
        string recipientEmail,
        string code,
        DateTimeOffset expiresAt,
        DateTimeOffset recordedAt)
    {
        InvitationId = invitationId;
        RecipientEmail = recipientEmail;
        Code = code;
        ExpiresAt = expiresAt;
        RecordedAt = recordedAt;
    }

    public Guid InvitationId { get; }

    public string RecipientEmail { get; }

    [JsonIgnore]
    public string Code { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset RecordedAt { get; }

    public override string ToString() =>
        $"{nameof(RecordedTenantInvitationDelivery)} {{ "
        + $"InvitationId = {InvitationId}, "
        + "RecipientEmail = [REDACTED], Code = [REDACTED], "
        + $"ExpiresAt = {ExpiresAt:O}, RecordedAt = {RecordedAt:O} }}";
}

public sealed class RecordingTenantInvitationDelivery :
    ITenantInvitationDelivery,
    IDisposable
{
    public const int Capacity = 100;

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<
        Guid,
        RecordedTenantInvitationDelivery> _deliveries = new();
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _cleanupTimer;
    private bool _disposed;

    public RecordingTenantInvitationDelivery(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _cleanupTimer = timeProvider.CreateTimer(
            static state =>
                ((RecordingTenantInvitationDelivery)state!).RemoveExpired(),
            this,
            CleanupInterval,
            CleanupInterval);
    }

    public int Count => _deliveries.Count;

    public Task DeliverAsync(
        Guid invitationId,
        string recipientEmail,
        string code,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invitationId == Guid.Empty
            || string.IsNullOrWhiteSpace(recipientEmail)
            || string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException(
                "A complete invitation delivery is required.");
        }

        var recorded = new RecordedTenantInvitationDelivery(
            invitationId,
            recipientEmail,
            code,
            expiresAt,
            _timeProvider.GetUtcNow());

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_deliveries.Count >= Capacity)
            {
                throw new InvalidOperationException(
                    "The invitation delivery recorder is at capacity.");
            }

            if (!_deliveries.TryAdd(invitationId, recorded))
            {
                throw new InvalidOperationException(
                    "An invitation delivery is already recorded for this identifier.");
            }
        }

        return Task.CompletedTask;
    }

    public bool TryTake(
        Guid invitationId,
        [NotNullWhen(true)] out RecordedTenantInvitationDelivery? delivery)
    {
        RecordedTenantInvitationDelivery? recorded;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_deliveries.TryRemove(invitationId, out recorded))
            {
                delivery = null;
                return false;
            }
        }

        if (IsExpired(recorded))
        {
            delivery = null;
            return false;
        }

        delivery = recorded;
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cleanupTimer.Dispose();
            _deliveries.Clear();
        }
    }

    private bool IsExpired(RecordedTenantInvitationDelivery delivery) =>
        _timeProvider.GetUtcNow() - delivery.RecordedAt >= AccessLifetime;

    private void RemoveExpired()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var delivery in _deliveries)
            {
                if (IsExpired(delivery.Value))
                {
                    _deliveries.TryRemove(delivery.Key, out _);
                }
            }
        }
    }
}
