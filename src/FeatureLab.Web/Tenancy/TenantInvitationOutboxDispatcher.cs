using System.Data;
using System.Security.Cryptography;
using System.Text;
using FeatureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Tenancy;

public sealed class TenantInvitationOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<TenantInvitationOutboxDispatcher> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _batchGate = new(1, 1);

    private int _nextBatchOffset;

    public const int BatchSize = 20;

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    public static readonly EventId DeliveryDeferredEvent = new(
        2101,
        "TenantInvitationDeliveryDeferred");

    public static readonly EventId MessageDiscardedEvent = new(
        2102,
        "TenantInvitationOutboxMessageDiscarded");

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Do not attach the exception: provider and persistence errors
                // can include a recipient or the invitation capability.
                logger.LogError(
                    DeliveryDeferredEvent,
                    "The tenant invitation outbox batch was deferred.");
            }

            try
            {
                await Task.Delay(
                    PollInterval,
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> ProcessBatchAsync(
        CancellationToken cancellationToken = default)
    {
        await _batchGate.WaitAsync(cancellationToken);
        try
        {
            return await ProcessBatchCoreAsync(cancellationToken);
        }
        finally
        {
            _batchGate.Release();
        }
    }

    private async Task<int> ProcessBatchCoreAsync(
        CancellationToken cancellationToken)
    {
        var invitationIds = await LoadBatchAsync(
            _nextBatchOffset,
            cancellationToken);
        if (invitationIds.Length == 0 && _nextBatchOffset > 0)
        {
            _nextBatchOffset = 0;
            invitationIds = await LoadBatchAsync(
                _nextBatchOffset,
                cancellationToken);
        }

        var retainedCount = 0;
        // This single-process sequential worker is deliberately claim-free.
        // Multi-node claims and durable retry scheduling belong to Episode 22.
        foreach (var invitationId in invitationIds)
        {
            if (await DispatchOneAsync(invitationId, cancellationToken))
            {
                retainedCount++;
            }
        }

        // Rows removed during this pass close the gap before the next page.
        // Advancing only by retained rows lets later work make progress when a
        // complete batch is waiting on a failing provider.
        _nextBatchOffset = invitationIds.Length == BatchSize
            ? _nextBatchOffset + retainedCount
            : 0;

        return invitationIds.Length;
    }

    private async Task<Guid[]> LoadBatchAsync(
        int offset,
        CancellationToken cancellationToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            return await dbContext.TenantInvitationOutboxMessages
                .AsNoTracking()
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.InvitationId)
                .Select(message => message.InvitationId)
                .Skip(offset)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);
        }
    }

    private async Task<bool> DispatchOneAsync(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        DeliverySnapshot? snapshot;
        TenantInvitationOutboxEnvelope? envelope;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var message = await dbContext.TenantInvitationOutboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.InvitationId == invitationId,
                    cancellationToken);
            if (message is null)
            {
                return false;
            }

            var invitation = await dbContext.TenantInvitations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == invitationId,
                    cancellationToken);
            snapshot = invitation is null
                ? null
                : new DeliverySnapshot(
                    invitation.Id,
                    invitation.TenantId,
                    invitation.NormalizedEmail,
                    invitation.CodeHash,
                    invitation.ExpiresAt,
                    invitation.ClosedAt);

            var protector = scope.ServiceProvider
                .GetRequiredService<ITenantInvitationOutboxProtector>();
            if (!protector.TryUnprotect(
                    message.ProtectedPayload,
                    out envelope))
            {
                await DiscardAsync(invitationId, cancellationToken);
                return false;
            }

            if (snapshot is null
                || message.TenantId != snapshot.TenantId
                || !Matches(snapshot, envelope!))
            {
                await DiscardAsync(invitationId, cancellationToken);
                return false;
            }
        }

        var now = timeProvider.GetUtcNow();
        if (snapshot.ClosedAt is not null || snapshot.ExpiresAt <= now)
        {
            await DiscardAsync(invitationId, cancellationToken);
            return false;
        }

        try
        {
            await using var deliveryScope = scopeFactory.CreateAsyncScope();
            var delivery = deliveryScope.ServiceProvider
                .GetRequiredService<ITenantInvitationDelivery>();
            using var timeout = new CancellationTokenSource(
                DeliveryTimeout,
                timeProvider);
            using var deliveryCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
            var deliveryTask = delivery.DeliverAsync(
                snapshot.InvitationId,
                envelope!.NormalizedRecipient,
                envelope.Code,
                envelope.ExpiresAt,
                deliveryCancellation.Token);
            ObserveLateFault(deliveryTask);
            await deliveryTask.WaitAsync(deliveryCancellation.Token);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A provider failure keeps the durable message pending. Its
            // exception is intentionally omitted because adapters can include
            // recipient data or the raw capability in exception messages.
            logger.LogWarning(
                DeliveryDeferredEvent,
                "Tenant invitation delivery was deferred for invitation {InvitationId}.",
                invitationId);
            return true;
        }

        await DeleteDeliveredAsync(invitationId, cancellationToken);
        return false;
    }

    private async Task DiscardAsync(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        var message = await dbContext.TenantInvitationOutboxMessages
            .SingleOrDefaultAsync(
                candidate => candidate.InvitationId == invitationId,
                cancellationToken);
        if (message is null)
        {
            return;
        }

        var invitation = await dbContext.TenantInvitations
            .SingleOrDefaultAsync(
                candidate => candidate.Id == invitationId,
                cancellationToken);
        if (invitation is { ClosedAt: null })
        {
            invitation.Close(timeProvider.GetUtcNow());
        }

        dbContext.TenantInvitationOutboxMessages.Remove(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning(
            MessageDiscardedEvent,
            "Discarded an undeliverable tenant invitation outbox message for invitation {InvitationId}.",
            invitationId);
    }

    private async Task DeleteDeliveredAsync(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var message = await dbContext.TenantInvitationOutboxMessages
            .SingleOrDefaultAsync(
                candidate => candidate.InvitationId == invitationId,
                cancellationToken);
        if (message is null)
        {
            return;
        }

        dbContext.TenantInvitationOutboxMessages.Remove(message);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool Matches(
        DeliverySnapshot snapshot,
        TenantInvitationOutboxEnvelope envelope) =>
        envelope.Version == TenantInvitationOutboxEnvelope.CurrentVersion
        && envelope.InvitationId == snapshot.InvitationId
        && envelope.TenantId == snapshot.TenantId
        && string.Equals(
            envelope.NormalizedRecipient,
            snapshot.NormalizedRecipient,
            StringComparison.Ordinal)
        && envelope.ExpiresAt == snapshot.ExpiresAt
        && CodeMatchesHash(envelope.Code, snapshot.CodeHash);

    private static bool CodeMatchesHash(string code, string storedHash)
    {
        try
        {
            var expected = Convert.FromHexString(storedHash);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(code));
            return expected.Length == actual.Length
                && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
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

    private sealed record DeliverySnapshot(
        Guid InvitationId,
        Guid TenantId,
        string NormalizedRecipient,
        string CodeHash,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ClosedAt);
}
