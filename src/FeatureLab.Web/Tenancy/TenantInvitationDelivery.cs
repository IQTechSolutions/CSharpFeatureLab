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

public enum IssueAndDeliverTenantInvitationStatus
{
    HandedOff,
    InvalidRecipient,
    ActiveMember,
    Conflict,
    StaleOwner,
    DeliveryFailedCompensated,
    DeliveryOutcomeUnknown,
}

public sealed class IssueAndDeliverTenantInvitationResult
{
    private IssueAndDeliverTenantInvitationResult(
        IssueAndDeliverTenantInvitationStatus status,
        Guid? id = null,
        DateTimeOffset? expiresAt = null)
    {
        Status = status;
        Id = id;
        ExpiresAt = expiresAt;
    }

    public IssueAndDeliverTenantInvitationStatus Status { get; }

    public Guid? Id { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public static IssueAndDeliverTenantInvitationResult HandedOff(
        Guid id,
        DateTimeOffset expiresAt) =>
        new(
            IssueAndDeliverTenantInvitationStatus.HandedOff,
            id,
            expiresAt);

    public static IssueAndDeliverTenantInvitationResult FromStatus(
        IssueAndDeliverTenantInvitationStatus status)
    {
        if (status == IssueAndDeliverTenantInvitationStatus.HandedOff)
        {
            throw new ArgumentException(
                "A handed-off result requires invitation metadata.",
                nameof(status));
        }

        return new(status);
    }
}

public sealed class TenantInvitationDeliveryService(
    ITenantInvitationStore invitations,
    ITenantInvitationDelivery delivery,
    TimeProvider timeProvider,
    ILogger<TenantInvitationDeliveryService> logger)
{
    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan CompensationTimeout = TimeSpan.FromSeconds(5);

    public static readonly EventId IssuanceOutcomeUnknownEvent = new(
        2001,
        "TenantInvitationIssuanceOutcomeUnknown");

    public static readonly EventId DeliveryOutcomeUnknownEvent = new(
        2002,
        "TenantInvitationDeliveryOutcomeUnknown");

    public async Task<IssueAndDeliverTenantInvitationResult>
        IssueAndDeliverForOwnerAsync(
            string userId,
            string securityStamp,
            long membershipVersion,
            Guid trustedTenantId,
            string requestedRecipientEmail,
            CancellationToken requestCancellationToken)
    {
        IssueTenantInvitationResult issueResult;
        try
        {
            issueResult = await invitations.IssueForOwnerAsync(
                userId,
                securityStamp,
                membershipVersion,
                trustedTenantId,
                requestedRecipientEmail,
                requestCancellationToken);
        }
        catch (OperationCanceledException)
            when (requestCancellationToken.IsCancellationRequested)
        {
            logger.LogCritical(
                IssuanceOutcomeUnknownEvent,
                "The tenant invitation issuance outcome is unknown.");
            throw;
        }
        catch (Exception)
        {
            logger.LogCritical(
                IssuanceOutcomeUnknownEvent,
                "The tenant invitation issuance outcome is unknown.");
            throw new InvalidOperationException(
                "The tenant invitation issuance outcome is unknown.");
        }

        if (issueResult.Status != IssueTenantInvitationStatus.Issued)
        {
            return IssueAndDeliverTenantInvitationResult.FromStatus(
                issueResult.Status switch
                {
                    IssueTenantInvitationStatus.InvalidRecipient
                        => IssueAndDeliverTenantInvitationStatus.InvalidRecipient,
                    IssueTenantInvitationStatus.ActiveMember
                        => IssueAndDeliverTenantInvitationStatus.ActiveMember,
                    IssueTenantInvitationStatus.Conflict
                        => IssueAndDeliverTenantInvitationStatus.Conflict,
                    IssueTenantInvitationStatus.StaleOwner
                        => IssueAndDeliverTenantInvitationStatus.StaleOwner,
                    _ => throw new InvalidOperationException(
                        "Unknown invitation issuance result."),
                });
        }

        var invitationId = issueResult.Id!.Value;
        if (!requestCancellationToken.IsCancellationRequested)
        {
            try
            {
                using var serverTimeout = new CancellationTokenSource(
                    DeliveryTimeout,
                    timeProvider);
                using var deliveryCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        requestCancellationToken,
                        serverTimeout.Token);
                var deliveryTask = delivery.DeliverAsync(
                    invitationId,
                    issueResult.RecipientEmail!,
                    issueResult.Code!,
                    issueResult.ExpiresAt!.Value,
                    deliveryCancellation.Token);
                ObserveLateFault(deliveryTask);
                await deliveryTask.WaitAsync(deliveryCancellation.Token);

                return IssueAndDeliverTenantInvitationResult.HandedOff(
                    invitationId,
                    issueResult.ExpiresAt.Value);
            }
            catch (Exception)
            {
                // Provider failures are intentionally not logged because their
                // messages can contain recipient data or the raw capability.
            }
        }

        return await CompensateAsync(trustedTenantId, invitationId);
    }

    private async Task<IssueAndDeliverTenantInvitationResult> CompensateAsync(
        Guid tenantId,
        Guid invitationId)
    {
        try
        {
            using var compensationCancellation = new CancellationTokenSource(
                CompensationTimeout,
                timeProvider);
            var compensationTask = invitations.CloseUndeliveredAsync(
                tenantId,
                invitationId,
                compensationCancellation.Token);
            ObserveLateFault(compensationTask);
            var compensation = await compensationTask.WaitAsync(
                compensationCancellation.Token);
            if (compensation
                == CloseUndeliveredTenantInvitationStatus.Closed)
            {
                return IssueAndDeliverTenantInvitationResult.FromStatus(
                    IssueAndDeliverTenantInvitationStatus
                        .DeliveryFailedCompensated);
            }
        }
        catch (Exception)
        {
            // The sanitized critical event below is the only emitted detail.
        }

        logger.LogCritical(
            DeliveryOutcomeUnknownEvent,
            "Tenant invitation delivery outcome is unknown for invitation {InvitationId}.",
            invitationId);
        return IssueAndDeliverTenantInvitationResult.FromStatus(
            IssueAndDeliverTenantInvitationStatus.DeliveryOutcomeUnknown);
    }

    private static void ObserveLateFault(Task operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
