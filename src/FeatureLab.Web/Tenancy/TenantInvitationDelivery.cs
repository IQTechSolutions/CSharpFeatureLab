using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
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

    public static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);

    public static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private static readonly byte[] FingerprintPurpose =
        "FeatureLab.TenantInvitationDelivery.Request.v1"u8.ToArray();

    private readonly Dictionary<Guid, AcceptedRequest> _acceptedRequests = [];
    private readonly ConcurrentDictionary<
        Guid,
        RecordedTenantInvitationDelivery> _deliveries = new();
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _cleanupTimer;
    private bool _disposed;
    private long _attemptCount;
    private long _logicalDeliveryCount;

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

    public long AttemptCount => Interlocked.Read(ref _attemptCount);

    public long LogicalDeliveryCount =>
        Interlocked.Read(ref _logicalDeliveryCount);

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

        var fingerprint = CreateFingerprint(
            recipientEmail,
            code,
            expiresAt);
        var fingerprintStored = false;

        lock (_sync)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = _timeProvider.GetUtcNow();
                RemoveExpiredCore(now);
                Interlocked.Increment(ref _attemptCount);

                if (_acceptedRequests.TryGetValue(
                        invitationId,
                        out var accepted))
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            accepted.Fingerprint,
                            fingerprint))
                    {
                        throw new InvalidOperationException(
                            "The invitation delivery idempotency key is already bound to different request content.");
                    }

                    return Task.CompletedTask;
                }

                if (_deliveries.Count >= Capacity)
                {
                    throw new InvalidOperationException(
                        "The invitation delivery recorder is at capacity.");
                }

                var recorded = new RecordedTenantInvitationDelivery(
                    invitationId,
                    recipientEmail,
                    code,
                    expiresAt,
                    now);
                if (!_deliveries.TryAdd(invitationId, recorded))
                {
                    throw new InvalidOperationException(
                        "An invitation delivery is already recorded for this identifier.");
                }

                _acceptedRequests.Add(
                    invitationId,
                    new AcceptedRequest(fingerprint, now));
                fingerprintStored = true;
                Interlocked.Increment(ref _logicalDeliveryCount);
            }
            finally
            {
                if (!fingerprintStored)
                {
                    CryptographicOperations.ZeroMemory(fingerprint);
                }
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
            foreach (var accepted in _acceptedRequests.Values)
            {
                CryptographicOperations.ZeroMemory(accepted.Fingerprint);
            }

            _acceptedRequests.Clear();
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

            RemoveExpiredCore(_timeProvider.GetUtcNow());
        }
    }

    private void RemoveExpiredCore(DateTimeOffset now)
    {
        foreach (var delivery in _deliveries)
        {
            if (now - delivery.Value.RecordedAt >= AccessLifetime)
            {
                _deliveries.TryRemove(delivery.Key, out _);
            }
        }

        foreach (var accepted in _acceptedRequests.ToArray())
        {
            if (now - accepted.Value.AcceptedAt < IdempotencyRetention)
            {
                continue;
            }

            if (_acceptedRequests.Remove(accepted.Key))
            {
                CryptographicOperations.ZeroMemory(
                    accepted.Value.Fingerprint);
            }
        }
    }

    private static byte[] CreateFingerprint(
        string recipientEmail,
        string code,
        DateTimeOffset expiresAt)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(FingerprintPurpose);
        AppendCanonicalString(hash, recipientEmail);
        AppendCanonicalString(hash, code);

        Span<byte> expiry = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(
            expiry,
            expiresAt.UtcDateTime.Ticks);
        hash.AppendData(expiry);
        return hash.GetHashAndReset();
    }

    private static void AppendCanonicalString(
        IncrementalHash hash,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record AcceptedRequest(
        byte[] Fingerprint,
        DateTimeOffset AcceptedAt);
}
