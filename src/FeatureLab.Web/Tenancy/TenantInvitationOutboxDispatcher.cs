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
        var dueAt = timeProvider.GetUtcNow().UtcDateTime;
        var invitationIds = await LoadBatchAsync(
            dueAt,
            cancellationToken);

        // This single-process sequential worker deliberately has no
        // distributed claim. Episode 24 adds that coordination boundary.
        foreach (var invitationId in invitationIds)
        {
            await DispatchOneAsync(invitationId, cancellationToken);
        }

        return invitationIds.Length;
    }

    private async Task<Guid[]> LoadBatchAsync(
        DateTime dueAt,
        CancellationToken cancellationToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            return await dbContext.TenantInvitationOutboxMessages
                .AsNoTracking()
                .Where(message => message.NextAttemptAt <= dueAt)
                .OrderBy(message => message.NextAttemptAt)
                .ThenBy(message => message.CreatedAt)
                .ThenBy(message => message.InvitationId)
                .Select(message => message.InvitationId)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);
        }
    }

    private async Task DispatchOneAsync(
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
                return;
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
                return;
            }

            if (snapshot is null
                || message.TenantId != snapshot.TenantId
                || !Matches(snapshot, envelope!))
            {
                await DiscardAsync(invitationId, cancellationToken);
                return;
            }
        }

        var now = timeProvider.GetUtcNow();
        if (snapshot.ClosedAt is not null || snapshot.ExpiresAt <= now)
        {
            await DiscardAsync(invitationId, cancellationToken);
            return;
        }

        await using var deliveryScope = scopeFactory.CreateAsyncScope();
        var delivery = deliveryScope.ServiceProvider
            .GetRequiredService<ITenantInvitationDelivery>();
        if (!await BeginAttemptAsync(invitationId, cancellationToken))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(
            DeliveryTimeout,
            timeProvider);
        using var deliveryCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
        try
        {
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
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested)
        {
            if (await ScheduleRetryAsync(
                invitationId,
                snapshot.ExpiresAt,
                TenantInvitationOutboxMessage.DeliveryTimeoutFailureCode,
                cancellationToken))
            {
                LogDeferred(invitationId);
            }

            return;
        }
        catch (Exception)
        {
            if (await ScheduleRetryAsync(
                invitationId,
                snapshot.ExpiresAt,
                TenantInvitationOutboxMessage.ProviderFailureCode,
                cancellationToken))
            {
                LogDeferred(invitationId);
            }

            return;
        }

        try
        {
            await DeleteDeliveredAsync(invitationId, cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (await ScheduleRetryAsync(
                invitationId,
                snapshot.ExpiresAt,
                TenantInvitationOutboxMessage.AcknowledgementFailureCode,
                cancellationToken))
            {
                LogDeferred(invitationId);
            }

            return;
        }
    }

    private async Task<bool> BeginAttemptAsync(
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
            return false;
        }

        message.BeginAttempt();
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ScheduleRetryAsync(
        Guid invitationId,
        DateTimeOffset expiresAt,
        string failureCode,
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
            return false;
        }

        message.ScheduleRetry(
            timeProvider.GetUtcNow(),
            expiresAt,
            failureCode);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void LogDeferred(Guid invitationId)
    {
        // Provider and persistence exceptions are intentionally omitted: an
        // adapter can include the recipient or raw capability in its message.
        logger.LogWarning(
            DeliveryDeferredEvent,
            "Tenant invitation delivery was deferred for invitation {InvitationId}.",
            invitationId);
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
