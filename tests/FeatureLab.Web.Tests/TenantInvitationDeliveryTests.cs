using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatureLab.Data;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeatureLab.Web.Tests;

public sealed class TenantInvitationDeliveryTests
{
    [Fact]
    public void Queued_result_contains_only_safe_metadata()
    {
        var result = IssueTenantInvitationResult.Queued(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(1));

        var json = JsonSerializer.Serialize(result);
        var text = result.ToString();

        Assert.Equal(IssueTenantInvitationStatus.Queued, result.Status);
        Assert.DoesNotContain("Code", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Recipient", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Code", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Recipient", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recorder_is_keyed_one_time_and_redacts_string_output()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        var invitationId = Guid.NewGuid();
        var recipient = NormalizedTestEmail("RECORDER");
        var code = "recorder-secret-capability";
        await recorder.DeliverAsync(
            invitationId,
            recipient,
            code,
            time.GetUtcNow().AddHours(1),
            default);

        Assert.True(recorder.TryTake(invitationId, out var delivery));
        Assert.Equal(code, delivery.Code);
        Assert.DoesNotContain(
            code,
            delivery.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            recipient,
            delivery.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(recorder.TryTake(invitationId, out _));
    }

    [Fact]
    public async Task Recorder_rejects_entries_at_access_limit()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        var invitationId = Guid.NewGuid();
        await recorder.DeliverAsync(
            invitationId,
            NormalizedTestEmail("EXPIRING"),
            "expiring-secret-capability",
            time.GetUtcNow().AddHours(1),
            default);

        time.AdvanceWithoutRunningTimers(
            RecordingTenantInvitationDelivery.AccessLifetime);

        Assert.False(recorder.TryTake(invitationId, out _));
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task Recorder_periodic_cleanup_removes_expired_entries()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        await recorder.DeliverAsync(
            Guid.NewGuid(),
            NormalizedTestEmail("CLEANUP"),
            "cleanup-secret-capability",
            time.GetUtcNow().AddHours(1),
            default);

        time.Advance(
            RecordingTenantInvitationDelivery.AccessLifetime
            + RecordingTenantInvitationDelivery.CleanupInterval);

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task Recorder_fails_at_capacity_without_evicting_a_secret()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        var invitationIds = Enumerable.Range(
                0,
                RecordingTenantInvitationDelivery.Capacity)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        foreach (var invitationId in invitationIds)
        {
            await recorder.DeliverAsync(
                invitationId,
                NormalizedTestEmail("CAPACITY"),
                $"capacity-secret-{invitationId:N}",
                time.GetUtcNow().AddHours(1),
                default);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.DeliverAsync(
                Guid.NewGuid(),
                NormalizedTestEmail("OVERFLOW"),
                "overflow-secret-capability",
                time.GetUtcNow().AddHours(1),
                default));

        Assert.Equal(
            RecordingTenantInvitationDelivery.Capacity,
            recorder.Count);
        Assert.DoesNotContain(
            "overflow-secret-capability",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.All(
            invitationIds,
            invitationId =>
                Assert.True(recorder.TryTake(invitationId, out _)));
    }

    [Fact]
    public async Task Recorder_enforces_capacity_under_concurrent_delivery()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        var invitationIds = Enumerable.Range(0, 150)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        var results = await Task.WhenAll(
            invitationIds.Select(async invitationId =>
            {
                try
                {
                    await Task.Run(() => recorder.DeliverAsync(
                        invitationId,
                        NormalizedTestEmail("CONCURRENT"),
                        $"concurrent-secret-{invitationId:N}",
                        time.GetUtcNow().AddHours(1),
                        default));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }));

        Assert.Equal(
            RecordingTenantInvitationDelivery.Capacity,
            results.Count(delivered => delivered));
        Assert.Equal(
            RecordingTenantInvitationDelivery.Capacity,
            recorder.Count);
    }

    [Fact]
    public async Task Owner_issue_saves_invitation_and_protected_envelope_together()
    {
        var capturingProtector = new CapturingProtector();
        await using var harness = await DispatcherHarness.CreateAsync(
            services =>
            {
                services.RemoveAll<ITenantInvitationOutboxProtector>();
                services.AddSingleton<ITenantInvitationOutboxProtector>(
                    capturingProtector);
            });
        var (owner, tenantId) = await harness.SeedOwnerAsync();

        IssueTenantInvitationResult result;
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider
                .GetRequiredService<ITenantInvitationStore>();
            result = await store.IssueForOwnerAsync(
                owner.Id,
                owner.SecurityStamp!,
                1,
                tenantId,
                $"  {TestEmail("RECIPIENT")}  ");
        }

        Assert.Equal(IssueTenantInvitationStatus.Queued, result.Status);
        Assert.NotNull(capturingProtector.Envelope);
        Assert.Equal(result.Id, capturingProtector.Envelope.InvitationId);
        Assert.Equal(tenantId, capturingProtector.Envelope.TenantId);
        Assert.Equal(
            NormalizedTestEmail("RECIPIENT"),
            capturingProtector.Envelope.NormalizedRecipient);
        Assert.False(string.IsNullOrWhiteSpace(capturingProtector.Envelope.Code));

        await using var verification = harness.Services.CreateAsyncScope();
        var dbContext = verification.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync();
        var message = await dbContext.TenantInvitationOutboxMessages
            .SingleAsync();
        Assert.Equal(invitation.Id, message.InvitationId);
        Assert.Equal("captured-protected-payload", message.ProtectedPayload);
        Assert.Equal(
            Hash(capturingProtector.Envelope.Code),
            invitation.CodeHash);
    }

    [Fact]
    public async Task Owner_issue_rolls_back_prior_changes_when_protection_fails()
    {
        await using var harness = await DispatcherHarness.CreateAsync(
            services =>
            {
                services.RemoveAll<ITenantInvitationOutboxProtector>();
                services.AddSingleton<ITenantInvitationOutboxProtector,
                    ThrowingProtector>();
            });
        var (owner, tenantId) = await harness.SeedOwnerAsync();
        var recipient = NormalizedTestEmail("ROLLBACK");
        var oldCode = "old-invitation-code-for-rollback";
        var expiredId = Guid.NewGuid();
        await harness.SeedInvitationAsync(
            expiredId,
            tenantId,
            recipient,
            oldCode,
            harness.Time.GetUtcNow().AddMinutes(-1),
            includeOutbox: false);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider
                .GetRequiredService<ITenantInvitationStore>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.IssueForOwnerAsync(
                    owner.Id,
                    owner.SecurityStamp!,
                    1,
                    tenantId,
                    recipient));
        }

        await using var verification = harness.Services.CreateAsyncScope();
        var dbContext = verification.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync();
        Assert.Equal(expiredId, invitation.Id);
        Assert.Null(invitation.ClosedAt);
        Assert.Equal(1, invitation.Version);
        Assert.Empty(dbContext.TenantInvitationOutboxMessages);
    }

    [Fact]
    public async Task Owner_issue_rolls_back_when_the_outbox_insert_fails()
    {
        await using var harness = await DispatcherHarness.CreateAsync();
        var (owner, tenantId) = await harness.SeedOwnerAsync();
        await using (var setup = harness.Services.CreateAsyncScope())
        {
            var dbContext = setup.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER FailTenantInvitationOutboxInsert
                BEFORE INSERT ON TenantInvitationOutbox
                BEGIN
                    SELECT RAISE(ABORT, 'simulated outbox insert failure');
                END;
                """);
        }

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider
                .GetRequiredService<ITenantInvitationStore>();
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                store.IssueForOwnerAsync(
                    owner.Id,
                    owner.SecurityStamp!,
                    1,
                    tenantId,
                    NormalizedTestEmail("SAVE-FAILURE")));
        }

        await using var verification = harness.Services.CreateAsyncScope();
        var verificationContext = verification.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        Assert.Empty(verificationContext.TenantInvitations);
        Assert.Empty(verificationContext.TenantInvitationOutboxMessages);
    }

    [Fact]
    public void Protector_encrypts_the_complete_time_limited_envelope()
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .UseEphemeralDataProtectionProvider()
            .SetApplicationName("FeatureLab.CSharpFeatureLab");
        services.AddSingleton<TenantInvitationOutboxProtector>();
        using var provider = services.BuildServiceProvider();
        var protector = provider
            .GetRequiredService<TenantInvitationOutboxProtector>();
        var envelope = NewEnvelope();

        var protectedPayload = protector.Protect(envelope);
        var opened = protector.TryUnprotect(
            protectedPayload,
            out var roundTripped);

        Assert.True(opened);
        Assert.Equal(envelope, roundTripped);
        Assert.DoesNotContain(
            envelope.Code,
            protectedPayload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            envelope.NormalizedRecipient,
            protectedPayload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            envelope.Code,
            envelope.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            envelope.NormalizedRecipient,
            envelope.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", envelope.ToString(), StringComparison.Ordinal);
        var wrongPurpose = provider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(TenantInvitationOutboxProtector.Purpose + ".wrong")
            .ToTimeLimitedDataProtector();
        Assert.Throws<CryptographicException>(() =>
            wrongPurpose.Unprotect(protectedPayload));

        var tampered = protectedPayload[..^1]
            + (protectedPayload[^1] == 'A' ? 'B' : 'A');
        Assert.False(protector.TryUnprotect(tampered, out _));
    }

    [Fact]
    public async Task Persisted_key_ring_dispatches_committed_work_after_restart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-outbox-restart-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "feature-lab.db");
        var keyRingPath = Path.Combine(root, "keys");
        Directory.CreateDirectory(keyRingPath);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var observation = new DeliveryObservation();
        var invitationId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var recipient = NormalizedTestEmail("RESTART");
        const string code = "restart-safe-invitation-capability";
        var expiresAt = time.GetUtcNow().AddHours(1);

        try
        {
            await using (var firstHost = CreateRestartServices(
                databasePath,
                keyRingPath,
                time,
                observation))
            {
                await using var scope = firstHost.CreateAsyncScope();
                var dbContext = scope.ServiceProvider
                    .GetRequiredService<FeatureLabDbContext>();
                await dbContext.Database.EnsureCreatedAsync();
                var protector = scope.ServiceProvider.GetRequiredService<
                    ITenantInvitationOutboxProtector>();
                var invitation = TenantInvitation.Create(
                    invitationId,
                    tenantId,
                    recipient,
                    Hash(code),
                    expiresAt);
                dbContext.TenantInvitations.Add(invitation);
                dbContext.TenantInvitationOutboxMessages.Add(
                    TenantInvitationOutboxMessage.Create(
                        invitationId,
                        tenantId,
                        protector.Protect(
                            new TenantInvitationOutboxEnvelope(
                                TenantInvitationOutboxEnvelope.CurrentVersion,
                                invitationId,
                                tenantId,
                                recipient,
                                code,
                                expiresAt)),
                        time.GetUtcNow()));
                await dbContext.SaveChangesAsync();
            }

            await using (var restartedHost = CreateRestartServices(
                databasePath,
                keyRingPath,
                time,
                observation))
            {
                var dispatcher = restartedHost.GetRequiredService<
                    TenantInvitationOutboxDispatcher>();
                await dispatcher.ProcessBatchAsync();

                var delivered = Assert.Single(observation.Deliveries);
                Assert.Equal(invitationId, delivered.InvitationId);
                Assert.Equal(code, delivered.Code);
                await using var scope = restartedHost.CreateAsyncScope();
                Assert.Empty(scope.ServiceProvider
                    .GetRequiredService<FeatureLabDbContext>()
                    .TenantInvitationOutboxMessages);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Dispatcher_delivers_outside_a_transaction_and_removes_message()
    {
        await using var harness = await DispatcherHarness.CreateAsync();
        var invitationId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var code = "success-capability-code";
        await harness.SeedInvitationAsync(
            invitationId,
            tenantId,
            NormalizedTestEmail("SUCCESS"),
            code,
            harness.Time.GetUtcNow().AddHours(1));

        var processed = await harness.Dispatcher.ProcessBatchAsync();

        Assert.Equal(1, processed);
        var delivery = Assert.Single(harness.Observation.Deliveries);
        Assert.Equal(invitationId, delivery.InvitationId);
        Assert.Equal(code, delivery.Code);
        Assert.False(delivery.TransactionObserved);
        Assert.True(delivery.OutboxRowObserved);
        Assert.Equal(1, harness.Observation.CreatedScopes);
        Assert.Equal(1, harness.Observation.DisposedScopes);
        Assert.False(await harness.OutboxExistsAsync(invitationId));
    }

    [Fact]
    public async Task Failed_delete_after_handoff_can_deliver_the_message_again()
    {
        await using var harness = await DispatcherHarness.CreateAsync();
        var invitationId = Guid.NewGuid();
        await harness.SeedInvitationAsync(
            invitationId,
            Guid.NewGuid(),
            NormalizedTestEmail("AT-LEAST-ONCE"),
            "duplicate-window-capability",
            harness.Time.GetUtcNow().AddHours(1));
        await using (var setup = harness.Services.CreateAsyncScope())
        {
            var dbContext = setup.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER FailTenantInvitationOutboxDelete
                BEFORE DELETE ON TenantInvitationOutbox
                BEGIN
                    SELECT RAISE(ABORT, 'simulated post-handoff delete failure');
                END;
                """);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            harness.Dispatcher.ProcessBatchAsync());
        Assert.Single(harness.Observation.Deliveries);
        Assert.True(await harness.OutboxExistsAsync(invitationId));

        await using (var repair = harness.Services.CreateAsyncScope())
        {
            var dbContext = repair.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER FailTenantInvitationOutboxDelete;");
        }
        await harness.Dispatcher.ProcessBatchAsync();

        Assert.Equal(2, harness.Observation.Deliveries.Count);
        Assert.False(await harness.OutboxExistsAsync(invitationId));
    }

    [Fact]
    public async Task Provider_failure_retains_message_and_logs_no_secrets()
    {
        await using var harness = await DispatcherHarness.CreateAsync(
            services =>
            {
                services.RemoveAll<ITenantInvitationDelivery>();
                services.AddScoped<ITenantInvitationDelivery,
                    FailingDelivery>();
            });
        var invitationId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var recipient = NormalizedTestEmail("FAILURE");
        var code = "provider-failure-capability";
        await harness.SeedInvitationAsync(
            invitationId,
            tenantId,
            recipient,
            code,
            harness.Time.GetUtcNow().AddHours(1));

        await harness.Dispatcher.ProcessBatchAsync();

        Assert.True(await harness.OutboxExistsAsync(invitationId));
        var entry = Assert.Single(harness.Logger.Entries);
        Assert.Equal(
            TenantInvitationOutboxDispatcher.DeliveryDeferredEvent,
            entry.EventId);
        Assert.DoesNotContain(code, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            recipient,
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Delivery_timeout_retains_message_without_waiting_for_adapter()
    {
        var nonCooperative = new NonCooperativeDelivery();
        await using var harness = await DispatcherHarness.CreateAsync(
            services =>
            {
                services.RemoveAll<ITenantInvitationDelivery>();
                services.AddSingleton<ITenantInvitationDelivery>(
                    nonCooperative);
            });
        var invitationId = Guid.NewGuid();
        var code = "timeout-secret-capability";
        await harness.SeedInvitationAsync(
            invitationId,
            Guid.NewGuid(),
            NormalizedTestEmail("TIMEOUT"),
            code,
            harness.Time.GetUtcNow().AddHours(1));

        var processing = harness.Dispatcher.ProcessBatchAsync();
        await nonCooperative.Started.Task;
        harness.Time.Advance(
            TenantInvitationOutboxDispatcher.DeliveryTimeout);
        await processing;
        nonCooperative.Completion.SetException(
            new InvalidOperationException(
                $"Late provider fault exposed {code}."));
        await Task.Yield();

        Assert.True(await harness.OutboxExistsAsync(invitationId));
        var entry = Assert.Single(harness.Logger.Entries);
        Assert.Equal(
            TenantInvitationOutboxDispatcher.DeliveryDeferredEvent,
            entry.EventId);
        Assert.DoesNotContain(code, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Host_cancellation_retains_the_message_for_restart()
    {
        var nonCooperative = new NonCooperativeDelivery();
        await using var harness = await DispatcherHarness.CreateAsync(
            services =>
            {
                services.RemoveAll<ITenantInvitationDelivery>();
                services.AddSingleton<ITenantInvitationDelivery>(
                    nonCooperative);
            });
        var invitationId = Guid.NewGuid();
        await harness.SeedInvitationAsync(
            invitationId,
            Guid.NewGuid(),
            NormalizedTestEmail("SHUTDOWN"),
            "shutdown-safe-capability",
            harness.Time.GetUtcNow().AddHours(1));
        using var stopping = new CancellationTokenSource();

        var processing = harness.Dispatcher.ProcessBatchAsync(stopping.Token);
        await nonCooperative.Started.Task;
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await processing);
        nonCooperative.Completion.SetResult();
        Assert.True(await harness.OutboxExistsAsync(invitationId));
        Assert.Empty(harness.Logger.Entries);
    }

    [Theory]
    [InlineData(EnvelopeMismatch.InvitationId)]
    [InlineData(EnvelopeMismatch.TenantId)]
    [InlineData(EnvelopeMismatch.Recipient)]
    [InlineData(EnvelopeMismatch.CodeHash)]
    [InlineData(EnvelopeMismatch.Expiry)]
    [InlineData(EnvelopeMismatch.Version)]
    public async Task Dispatcher_fail_closes_every_envelope_mismatch(
        EnvelopeMismatch mismatch)
    {
        var invitationId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var recipient = NormalizedTestEmail("MISMATCH");
        var code = "matching-capability-code";
        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);
        var envelope = new TenantInvitationOutboxEnvelope(
            mismatch == EnvelopeMismatch.Version
                ? TenantInvitationOutboxEnvelope.CurrentVersion + 1
                : TenantInvitationOutboxEnvelope.CurrentVersion,
            mismatch == EnvelopeMismatch.InvitationId
                ? Guid.NewGuid()
                : invitationId,
            mismatch == EnvelopeMismatch.TenantId
                ? Guid.NewGuid()
                : tenantId,
            mismatch == EnvelopeMismatch.Recipient
                ? NormalizedTestEmail("OTHER")
                : recipient,
            mismatch == EnvelopeMismatch.CodeHash
                ? "different-capability-code"
                : code,
            mismatch == EnvelopeMismatch.Expiry
                ? expiresAt.AddMinutes(1)
                : expiresAt);
        await using var harness = await DispatcherHarness.CreateAsync(
            services =>
            {
                services.RemoveAll<ITenantInvitationOutboxProtector>();
                services.AddSingleton<ITenantInvitationOutboxProtector>(
                    new StaticProtector(envelope));
            });
        await harness.SeedInvitationAsync(
            invitationId,
            tenantId,
            recipient,
            code,
            expiresAt,
            protectedPayload: "protected-test-payload");

        await harness.Dispatcher.ProcessBatchAsync();

        Assert.False(await harness.OutboxExistsAsync(invitationId));
        Assert.True(await harness.InvitationIsClosedAsync(invitationId));
        var entry = Assert.Single(harness.Logger.Entries);
        Assert.Equal(
            TenantInvitationOutboxDispatcher.MessageDiscardedEvent,
            entry.EventId);
        Assert.Contains(
            invitationId.ToString(),
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(code, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            recipient,
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Corrupt_payload_is_closed_and_deleted_with_safe_logging()
    {
        await using var harness = await DispatcherHarness.CreateAsync();
        var invitationId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var recipient = NormalizedTestEmail("CORRUPT");
        var code = "corrupt-capability-code";
        await harness.SeedInvitationAsync(
            invitationId,
            tenantId,
            recipient,
            code,
            harness.Time.GetUtcNow().AddHours(1),
            protectedPayload: "not-a-data-protection-payload");

        await harness.Dispatcher.ProcessBatchAsync();

        Assert.False(await harness.OutboxExistsAsync(invitationId));
        Assert.True(await harness.InvitationIsClosedAsync(invitationId));
        var entry = Assert.Single(harness.Logger.Entries);
        Assert.DoesNotContain(code, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            recipient,
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(entry.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_or_expired_invitation_is_discarded(
        bool closeBeforeDispatch)
    {
        await using var harness = await DispatcherHarness.CreateAsync();
        var invitationId = Guid.NewGuid();
        var expiresAt = harness.Time.GetUtcNow().AddMinutes(1);
        await harness.SeedInvitationAsync(
            invitationId,
            Guid.NewGuid(),
            NormalizedTestEmail("STALE"),
            "stale-capability-code",
            expiresAt,
            closeBeforeDispatch: closeBeforeDispatch);
        if (!closeBeforeDispatch)
        {
            harness.Time.AdvanceWithoutRunningTimers(TimeSpan.FromMinutes(2));
        }

        await harness.Dispatcher.ProcessBatchAsync();

        Assert.False(await harness.OutboxExistsAsync(invitationId));
        Assert.True(await harness.InvitationIsClosedAsync(invitationId));
        Assert.Empty(harness.Observation.Deliveries);
    }

    [Fact]
    public async Task Dispatcher_processes_a_bounded_deterministic_batch()
    {
        await using var harness = await DispatcherHarness.CreateAsync();
        var ids = Enumerable.Range(
                0,
                TenantInvitationOutboxDispatcher.BatchSize + 1)
            .Select(_ => Guid.NewGuid())
            .OrderBy(id => id.ToString(), StringComparer.Ordinal)
            .ToArray();
        foreach (var id in ids.Reverse())
        {
            await harness.SeedInvitationAsync(
                id,
                Guid.NewGuid(),
                $"BATCH-{id:N}@EXAMPLE.TEST",
                $"batch-capability-{id:N}",
                harness.Time.GetUtcNow().AddHours(1));
        }

        var processed = await harness.Dispatcher.ProcessBatchAsync();

        Assert.Equal(TenantInvitationOutboxDispatcher.BatchSize, processed);
        Assert.Equal(
            ids.Take(TenantInvitationOutboxDispatcher.BatchSize),
            harness.Observation.Deliveries.Select(item => item.InvitationId));
        Assert.True(await harness.OutboxExistsAsync(ids[^1]));
    }

    [Fact]
    public async Task Retained_full_batch_does_not_starve_later_work()
    {
        await using var harness = await DispatcherHarness.CreateAsync(
            services =>
            {
                services.RemoveAll<ITenantInvitationDelivery>();
                services.AddScoped<ITenantInvitationDelivery,
                    SelectivelyFailingDelivery>();
            });
        var retainedIds = new List<Guid>();
        for (var index = 0;
             index < TenantInvitationOutboxDispatcher.BatchSize;
             index++)
        {
            var invitationId = Guid.NewGuid();
            retainedIds.Add(invitationId);
            await harness.SeedInvitationAsync(
                invitationId,
                Guid.NewGuid(),
                $"FAIL-{index:D2}@EXAMPLE.TEST",
                $"retained-capability-{index:D2}",
                harness.Time.GetUtcNow().AddHours(1));
            harness.Time.AdvanceWithoutRunningTimers(
                TimeSpan.FromMilliseconds(1));
        }

        var laterInvitationId = Guid.NewGuid();
        await harness.SeedInvitationAsync(
            laterInvitationId,
            Guid.NewGuid(),
            NormalizedTestEmail("LATER"),
            "later-capability",
            harness.Time.GetUtcNow().AddHours(1));

        var firstPass = await harness.Dispatcher.ProcessBatchAsync();
        var secondPass = await harness.Dispatcher.ProcessBatchAsync();

        Assert.Equal(TenantInvitationOutboxDispatcher.BatchSize, firstPass);
        Assert.Equal(1, secondPass);
        Assert.Equal(
            laterInvitationId,
            Assert.Single(harness.Observation.Deliveries).InvitationId);
        Assert.False(await harness.OutboxExistsAsync(laterInvitationId));
        foreach (var retainedId in retainedIds)
        {
            Assert.True(await harness.OutboxExistsAsync(retainedId));
        }
    }

    [Fact]
    public async Task Dispatcher_resolves_each_adapter_in_a_fresh_scope()
    {
        await using var harness = await DispatcherHarness.CreateAsync();
        var invitationIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var invitationId in invitationIds)
        {
            await harness.SeedInvitationAsync(
                invitationId,
                Guid.NewGuid(),
                $"SCOPE-{invitationId:N}@EXAMPLE.TEST",
                $"scope-capability-{invitationId:N}",
                harness.Time.GetUtcNow().AddHours(1));
        }

        await harness.Dispatcher.ProcessBatchAsync();

        Assert.Equal(2, harness.Observation.CreatedScopes);
        Assert.Equal(2, harness.Observation.DisposedScopes);
        Assert.Equal(
            invitationIds.OrderBy(id => id.ToString(), StringComparer.Ordinal),
            harness.Observation.Deliveries
                .Select(delivery => delivery.InvitationId));
    }

    [Fact]
    public void Production_startup_requires_an_explicit_delivery_adapter()
    {
        using var factory = new ProductionFeatureLabFactory(
            registerDelivery: false);

        var exception = Assert.ThrowsAny<Exception>(factory.CreateClient);

        Assert.Contains(
            nameof(ITenantInvitationDelivery),
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_startup_accepts_an_explicit_delivery_adapter()
    {
        using var factory = new ProductionFeatureLabFactory(
            registerDelivery: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/about");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<PassiveDelivery>(
            factory.Services.GetRequiredService<ITenantInvitationDelivery>());
    }

    private static TenantInvitationOutboxEnvelope NewEnvelope() =>
        new(
            TenantInvitationOutboxEnvelope.CurrentVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            NormalizedTestEmail("RECIPIENT"),
            "secret-invitation-capability",
            DateTimeOffset.UtcNow.AddHours(1));

    private static string Hash(string code) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string TestEmail(string localPart) =>
        $"{localPart}@example.test";

    private static string NormalizedTestEmail(string localPart) =>
        TestEmail(localPart).ToUpperInvariant();

    private static ServiceProvider CreateRestartServices(
        string databasePath,
        string keyRingPath,
        TimeProvider timeProvider,
        DeliveryObservation observation)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddSingleton(observation);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider =>
            provider.GetRequiredService<TenantContext>());
        services.AddDbContext<FeatureLabDbContext>(options =>
            options.UseSqlite(
                $"Data Source={databasePath};Pooling=False"));
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .SetApplicationName("FeatureLab.CSharpFeatureLab");
        services.AddSingleton<
            ITenantInvitationOutboxProtector,
            TenantInvitationOutboxProtector>();
        services.AddScoped<ITenantInvitationDelivery, InspectingDelivery>();
        services.AddSingleton<TenantInvitationOutboxDispatcher>();
        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
    }

    public enum EnvelopeMismatch
    {
        InvitationId,
        TenantId,
        Recipient,
        CodeHash,
        Expiry,
        Version,
    }

    private sealed class DispatcherHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DispatcherHarness(
            SqliteConnection connection,
            ServiceProvider services,
            ManualTimeProvider time,
            DeliveryObservation observation,
            RecordingLogger<TenantInvitationOutboxDispatcher> logger)
        {
            _connection = connection;
            Services = services;
            Time = time;
            Observation = observation;
            Logger = logger;
            Dispatcher = ActivatorUtilities.CreateInstance<
                TenantInvitationOutboxDispatcher>(services);
        }

        public ServiceProvider Services { get; }

        public ManualTimeProvider Time { get; }

        public DeliveryObservation Observation { get; }

        public RecordingLogger<TenantInvitationOutboxDispatcher> Logger
        {
            get;
        }

        public TenantInvitationOutboxDispatcher Dispatcher { get; }

        public static async Task<DispatcherHarness> CreateAsync(
            Action<IServiceCollection>? configure = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var observation = new DeliveryObservation();
            var logger =
                new RecordingLogger<TenantInvitationOutboxDispatcher>();
            var services = new ServiceCollection();
            services.AddSingleton(connection);
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton(observation);
            services.AddSingleton<
                ILogger<TenantInvitationOutboxDispatcher>>(logger);
            services.AddScoped<TenantContext>();
            services.AddScoped<ITenantContext>(provider =>
                provider.GetRequiredService<TenantContext>());
            services.AddDbContext<FeatureLabDbContext>((provider, options) =>
                options.UseSqlite(
                    provider.GetRequiredService<SqliteConnection>()));
            services.AddDataProtection()
                .UseEphemeralDataProtectionProvider()
                .SetApplicationName("FeatureLab.CSharpFeatureLab");
            services.AddSingleton<
                ITenantInvitationOutboxProtector,
                TenantInvitationOutboxProtector>();
            services.AddSingleton<ILookupNormalizer,
                UpperInvariantLookupNormalizer>();
            services.AddScoped<ITenantInvitationStore,
                EfTenantInvitationStore>();
            services.AddScoped<ITenantInvitationDelivery,
                InspectingDelivery>();
            configure?.Invoke(services);
            var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });
            await using (var scope = provider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider
                    .GetRequiredService<FeatureLabDbContext>();
                await dbContext.Database.EnsureCreatedAsync();
            }

            return new DispatcherHarness(
                connection,
                provider,
                time,
                observation,
                logger);
        }

        public async Task<(FeatureLabUser Owner, Guid TenantId)>
            SeedOwnerAsync()
        {
            var tenantId = Guid.NewGuid();
            var owner = new FeatureLabUser
            {
                Id = $"owner-{Guid.NewGuid():N}",
                UserName = $"owner-{Guid.NewGuid():N}@example.test",
                NormalizedUserName = $"OWNER-{Guid.NewGuid():N}@EXAMPLE.TEST",
                Email = $"owner-{Guid.NewGuid():N}@example.test",
                NormalizedEmail = $"OWNER-{Guid.NewGuid():N}@EXAMPLE.TEST",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
            };
            await using var scope = Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            dbContext.Users.Add(owner);
            dbContext.TenantMemberships.Add(
                TenantMembershipRecord.Create(
                    owner.Id,
                    tenantId,
                    TenantMembershipRole.Owner,
                    Time.GetUtcNow()));
            await dbContext.SaveChangesAsync();
            return (owner, tenantId);
        }

        public async Task SeedInvitationAsync(
            Guid invitationId,
            Guid tenantId,
            string normalizedRecipient,
            string code,
            DateTimeOffset expiresAt,
            bool includeOutbox = true,
            string? protectedPayload = null,
            bool closeBeforeDispatch = false)
        {
            var invitation = TenantInvitation.Create(
                invitationId,
                tenantId,
                normalizedRecipient,
                Hash(code),
                expiresAt);
            if (closeBeforeDispatch)
            {
                invitation.Close(Time.GetUtcNow());
            }

            await using var scope = Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            dbContext.TenantInvitations.Add(invitation);
            if (includeOutbox)
            {
                var payload = protectedPayload;
                if (payload is null)
                {
                    var protector = scope.ServiceProvider.GetRequiredService<
                        ITenantInvitationOutboxProtector>();
                    payload = protector.Protect(
                        new TenantInvitationOutboxEnvelope(
                            TenantInvitationOutboxEnvelope.CurrentVersion,
                            invitationId,
                            tenantId,
                            normalizedRecipient,
                            code,
                            expiresAt));
                }

                dbContext.TenantInvitationOutboxMessages.Add(
                    TenantInvitationOutboxMessage.Create(
                        invitationId,
                        tenantId,
                        payload,
                        Time.GetUtcNow()));
            }

            await dbContext.SaveChangesAsync();
        }

        public async Task<bool> OutboxExistsAsync(Guid invitationId)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>()
                .TenantInvitationOutboxMessages
                .AnyAsync(message => message.InvitationId == invitationId);
        }

        public async Task<bool> InvitationIsClosedAsync(Guid invitationId)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>()
                .TenantInvitations
                .Where(invitation => invitation.Id == invitationId)
                .Select(invitation => invitation.ClosedAt != null)
                .SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Dispatcher.Dispose();
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class DeliveryObservation
    {
        public int CreatedScopes;

        public int DisposedScopes;

        public ConcurrentQueue<ObservedDelivery> Deliveries { get; } = [];
    }

    private sealed class InspectingDelivery :
        ITenantInvitationDelivery,
        IDisposable
    {
        private readonly FeatureLabDbContext _dbContext;
        private readonly DeliveryObservation _observation;

        public InspectingDelivery(
            FeatureLabDbContext dbContext,
            DeliveryObservation observation)
        {
            _dbContext = dbContext;
            _observation = observation;
            Interlocked.Increment(ref observation.CreatedScopes);
        }

        public async Task DeliverAsync(
            Guid invitationId,
            string recipientEmail,
            string code,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            var transactionObserved =
                _dbContext.Database.CurrentTransaction is not null;
            var rowObserved = await _dbContext
                .TenantInvitationOutboxMessages
                .AnyAsync(
                    message => message.InvitationId == invitationId,
                    cancellationToken);
            _observation.Deliveries.Enqueue(
                new ObservedDelivery(
                    invitationId,
                    recipientEmail,
                    code,
                    expiresAt,
                    transactionObserved,
                    rowObserved));
        }

        public void Dispose() =>
            Interlocked.Increment(ref _observation.DisposedScopes);
    }

    private sealed record ObservedDelivery(
        Guid InvitationId,
        string Recipient,
        string Code,
        DateTimeOffset ExpiresAt,
        bool TransactionObserved,
        bool OutboxRowObserved);

    private sealed class FailingDelivery : ITenantInvitationDelivery
    {
        public Task DeliverAsync(
            Guid invitationId,
            string recipientEmail,
            string code,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Provider exposed {recipientEmail} and {code}.");
    }

    private sealed class SelectivelyFailingDelivery(
        DeliveryObservation observation) : ITenantInvitationDelivery
    {
        public Task DeliverAsync(
            Guid invitationId,
            string recipientEmail,
            string code,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            if (recipientEmail.StartsWith("FAIL-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The simulated provider rejected this delivery.");
            }

            observation.Deliveries.Enqueue(
                new ObservedDelivery(
                    invitationId,
                    recipientEmail,
                    code,
                    expiresAt,
                    TransactionObserved: false,
                    OutboxRowObserved: true));
            return Task.CompletedTask;
        }
    }

    private sealed class NonCooperativeDelivery :
        ITenantInvitationDelivery
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(
            Guid invitationId,
            string recipientEmail,
            string code,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            return Completion.Task;
        }
    }

    private sealed class CapturingProtector :
        ITenantInvitationOutboxProtector
    {
        public TenantInvitationOutboxEnvelope Envelope { get; private set; } =
            null!;

        public string Protect(TenantInvitationOutboxEnvelope envelope)
        {
            Envelope = envelope;
            return "captured-protected-payload";
        }

        public bool TryUnprotect(
            string protectedPayload,
            out TenantInvitationOutboxEnvelope? envelope)
        {
            envelope = Envelope;
            return true;
        }
    }

    private sealed class StaticProtector(
        TenantInvitationOutboxEnvelope envelope) :
        ITenantInvitationOutboxProtector
    {
        public string Protect(TenantInvitationOutboxEnvelope value) =>
            "protected-test-payload";

        public bool TryUnprotect(
            string protectedPayload,
            out TenantInvitationOutboxEnvelope? value)
        {
            value = envelope;
            return true;
        }
    }

    private sealed class ThrowingProtector :
        ITenantInvitationOutboxProtector
    {
        public string Protect(TenantInvitationOutboxEnvelope envelope) =>
            throw new InvalidOperationException("Protection unavailable.");

        public bool TryUnprotect(
            string protectedPayload,
            out TenantInvitationOutboxEnvelope? envelope)
        {
            envelope = null;
            return false;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(
                new LogEntry(
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    exception));
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception);

    private sealed class PassiveDelivery : ITenantInvitationDelivery
    {
        public Task DeliverAsync(
            Guid invitationId,
            string recipientEmail,
            string code,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ProductionFeatureLabFactory(bool registerDelivery) :
        WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-production-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting(
                "ConnectionStrings:FeatureLab",
                $"Data Source={_databasePath};Pooling=False");
            builder.UseSetting(
                "ConnectionStrings:BackgroundJobs",
                "Server=(localdb)\\MSSQLLocalDB;Database=FeatureLabDeliveryTests;Trusted_Connection=True;TrustServerCertificate=True");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                if (registerDelivery)
                {
                    services.AddSingleton<ITenantInvitationDelivery,
                        PassiveDelivery>();
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
    }
}
