using System.Net;
using System.Text.Json;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeatureLab.Web.Tests;

public sealed class TenantInvitationDeliveryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issuance_results_redact_and_do_not_serialize_codes()
    {
        var invitationId = Guid.NewGuid();
        var recipient = UpperRecipient("normalized");
        const string code = "secret-invitation-capability";
        var expiresAt = Now.AddHours(24);
        var issued = new IssuedTenantInvitation(
            invitationId,
            recipient,
            code,
            Guid.NewGuid(),
            expiresAt);
        var ownerResult = IssueTenantInvitationResult.Issued(
            invitationId,
            recipient,
            code,
            expiresAt);

        foreach (var result in new object[] { issued, ownerResult })
        {
            var json = JsonSerializer.Serialize(result);
            var text = result.ToString();

            Assert.DoesNotContain(code, json, StringComparison.Ordinal);
            Assert.DoesNotContain("Code", json, StringComparison.Ordinal);
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);
            Assert.DoesNotContain(recipient, text, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Recorder_is_keyed_one_time_and_redacts_string_output()
    {
        var time = new ManualTimeProvider(Now);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await recorder.DeliverAsync(
            firstId,
            UpperRecipient("first"),
            "first-secret-code",
            Now.AddHours(1),
            default);
        await recorder.DeliverAsync(
            secondId,
            UpperRecipient("second"),
            "second-secret-code",
            Now.AddHours(1),
            default);

        Assert.True(recorder.TryTake(firstId, out var first));
        Assert.Equal("first-secret-code", first.Code);
        Assert.DoesNotContain(
            first.Code,
            first.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            first.RecipientEmail,
            first.ToString(),
            StringComparison.Ordinal);
        Assert.False(recorder.TryTake(firstId, out _));
        Assert.Equal(1, recorder.Count);
        Assert.True(recorder.TryTake(secondId, out var second));
        Assert.Equal("second-secret-code", second.Code);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task Recorder_rejects_entries_at_the_five_minute_access_limit()
    {
        var time = new ManualTimeProvider(Now);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        var invitationId = Guid.NewGuid();
        await recorder.DeliverAsync(
            invitationId,
            UpperRecipient("recipient"),
            "secret-code",
            Now.AddHours(1),
            default);

        time.AdvanceWithoutRunningTimers(
            RecordingTenantInvitationDelivery.AccessLifetime);

        Assert.False(recorder.TryTake(invitationId, out _));
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task Recorder_periodic_cleanup_removes_expired_entries()
    {
        var time = new ManualTimeProvider(Now);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        await recorder.DeliverAsync(
            Guid.NewGuid(),
            UpperRecipient("recipient"),
            "secret-code",
            Now.AddHours(1),
            default);

        time.Advance(
            RecordingTenantInvitationDelivery.AccessLifetime
            + RecordingTenantInvitationDelivery.CleanupInterval);

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task Recorder_fails_at_capacity_without_evicting_a_secret()
    {
        var time = new ManualTimeProvider(Now);
        using var recorder = new RecordingTenantInvitationDelivery(time);
        var recordedIds = Enumerable.Range(
                0,
                RecordingTenantInvitationDelivery.Capacity)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        foreach (var invitationId in recordedIds)
        {
            await recorder.DeliverAsync(
                invitationId,
                UpperRecipient("recipient"),
                $"secret-{invitationId:N}",
                Now.AddHours(1),
                default);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.DeliverAsync(
                Guid.NewGuid(),
                UpperRecipient("overflow"),
                "overflow-secret",
                Now.AddHours(1),
                default));

        Assert.Equal(
            RecordingTenantInvitationDelivery.Capacity,
            recorder.Count);
        Assert.DoesNotContain(
            "overflow-secret",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.All(
            recordedIds,
            invitationId =>
                Assert.True(recorder.TryTake(invitationId, out _)));
    }

    [Fact]
    public async Task Recorder_enforces_capacity_under_concurrent_delivery()
    {
        var time = new ManualTimeProvider(Now);
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
                        UpperRecipient("recipient"),
                        $"secret-{invitationId:N}",
                        Now.AddHours(1),
                        default));
                    return (InvitationId: invitationId, Delivered: true);
                }
                catch (InvalidOperationException)
                {
                    return (InvitationId: invitationId, Delivered: false);
                }
            }));

        Assert.Equal(
            RecordingTenantInvitationDelivery.Capacity,
            results.Count(result => result.Delivered));
        Assert.Equal(
            invitationIds.Length
                - RecordingTenantInvitationDelivery.Capacity,
            results.Count(result => !result.Delivered));
        Assert.Equal(
            RecordingTenantInvitationDelivery.Capacity,
            recorder.Count);

        var consumed = await Task.WhenAll(
            results.Where(result => result.Delivered)
                .Select(result => Task.Run(() =>
                    recorder.TryTake(result.InvitationId, out _))));
        Assert.All(consumed, Assert.True);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task Coordinator_delivers_the_server_normalized_recipient()
    {
        var invitationId = Guid.NewGuid();
        var expiresAt = Now.AddHours(24);
        var store = new StubInvitationStore(
            IssueTenantInvitationResult.Issued(
                invitationId,
                UpperRecipient("normalized"),
                "secret-code",
                expiresAt));
        var delivery = new StubDelivery();
        var logger = new RecordingLogger<TenantInvitationDeliveryService>();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            new ManualTimeProvider(Now),
            logger);

        var result = await service.IssueAndDeliverForOwnerAsync(
            "owner",
            "stamp",
            1,
            Guid.NewGuid(),
            $"  {Recipient("browser-input")}  ",
            default);

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus.HandedOff,
            result.Status);
        Assert.Equal(invitationId, result.Id);
        Assert.Equal(expiresAt, result.ExpiresAt);
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(
            UpperRecipient("normalized"),
            delivery.RecipientEmail);
        Assert.Equal("secret-code", delivery.Code);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(
        IssueTenantInvitationStatus.InvalidRecipient,
        IssueAndDeliverTenantInvitationStatus.InvalidRecipient)]
    [InlineData(
        IssueTenantInvitationStatus.ActiveMember,
        IssueAndDeliverTenantInvitationStatus.ActiveMember)]
    [InlineData(
        IssueTenantInvitationStatus.Conflict,
        IssueAndDeliverTenantInvitationStatus.Conflict)]
    [InlineData(
        IssueTenantInvitationStatus.StaleOwner,
        IssueAndDeliverTenantInvitationStatus.StaleOwner)]
    public async Task Coordinator_maps_nonissued_results_without_delivery(
        IssueTenantInvitationStatus storeStatus,
        IssueAndDeliverTenantInvitationStatus expectedStatus)
    {
        var store = new StubInvitationStore(
            IssueTenantInvitationResult.FromStatus(storeStatus));
        var delivery = new StubDelivery();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            new ManualTimeProvider(Now),
            new RecordingLogger<TenantInvitationDeliveryService>());

        var result = await service.IssueAndDeliverForOwnerAsync(
            "owner",
            "stamp",
            1,
            Guid.NewGuid(),
            Recipient("recipient"),
            default);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, delivery.Calls);
        Assert.Equal(0, store.CloseCalls);
    }

    [Fact]
    public async Task Delivery_failure_with_closed_compensation_is_retryable()
    {
        var store = NewIssuedStore();
        store.CloseHandler = (_, _, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult(
                CloseUndeliveredTenantInvitationStatus.Closed);
        };
        var delivery = new StubDelivery
        {
            Handler = _ => throw new InvalidOperationException(
                $"provider included secret-code and {Recipient("recipient")}"),
        };
        var logger = new RecordingLogger<TenantInvitationDeliveryService>();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            new ManualTimeProvider(Now),
            logger);

        var result = await IssueAsync(service);

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus
                .DeliveryFailedCompensated,
            result.Status);
        Assert.Equal(1, store.CloseCalls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task No_longer_open_compensation_returns_unknown_and_logs_only_safe_data()
    {
        var invitationId = Guid.NewGuid();
        var store = NewIssuedStore(invitationId);
        store.CloseHandler = (_, _, _) => Task.FromResult(
            CloseUndeliveredTenantInvitationStatus.NoLongerOpen);
        var delivery = new StubDelivery
        {
            Handler = _ => throw new InvalidOperationException(
                $"provider included secret-code and {Recipient("recipient")}"),
        };
        var logger = new RecordingLogger<TenantInvitationDeliveryService>();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            new ManualTimeProvider(Now),
            logger);

        var result = await IssueAsync(service);

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus.DeliveryOutcomeUnknown,
            result.Status);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(
            TenantInvitationDeliveryService.DeliveryOutcomeUnknownEvent,
            entry.EventId);
        Assert.Contains(
            invitationId.ToString(),
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "secret-code",
            entry.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Recipient("recipient"),
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Request_cancellation_after_issuance_skips_delivery_and_compensates_independently()
    {
        var store = NewIssuedStore();
        store.CloseHandler = (_, _, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult(
                CloseUndeliveredTenantInvitationStatus.Closed);
        };
        var delivery = new StubDelivery();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            new ManualTimeProvider(Now),
            new RecordingLogger<TenantInvitationDeliveryService>());
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();

        var result = await service.IssueAndDeliverForOwnerAsync(
            "owner",
            "stamp",
            1,
            Guid.NewGuid(),
            Recipient("recipient"),
            requestCancellation.Token);

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus
                .DeliveryFailedCompensated,
            result.Status);
        Assert.Equal(0, delivery.Calls);
        Assert.Equal(1, store.CloseCalls);
    }

    [Fact]
    public async Task Delivery_timeout_uses_time_provider_then_compensates()
    {
        var time = new ManualTimeProvider(Now);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = NewIssuedStore();
        store.CloseHandler = (_, _, _) => Task.FromResult(
            CloseUndeliveredTenantInvitationStatus.Closed);
        var delivery = new StubDelivery
        {
            Handler = async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            time,
            new RecordingLogger<TenantInvitationDeliveryService>());

        var resultTask = IssueAsync(service);
        await started.Task;
        time.Advance(TenantInvitationDeliveryService.DeliveryTimeout);
        var result = await resultTask;

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus
                .DeliveryFailedCompensated,
            result.Status);
        Assert.Equal(1, store.CloseCalls);
    }

    [Fact]
    public async Task Delivery_timeout_does_not_wait_for_a_non_cooperative_adapter()
    {
        var time = new ManualTimeProvider(Now);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = NewIssuedStore();
        store.CloseHandler = (_, _, _) => Task.FromResult(
            CloseUndeliveredTenantInvitationStatus.Closed);
        var delivery = new StubDelivery
        {
            Handler = _ =>
            {
                started.SetResult();
                return neverCompletes.Task;
            },
        };
        var logger = new RecordingLogger<TenantInvitationDeliveryService>();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            time,
            logger);

        var resultTask = IssueAsync(service);
        await started.Task;
        time.Advance(TenantInvitationDeliveryService.DeliveryTimeout);
        var result = await resultTask;
        neverCompletes.SetException(new InvalidOperationException(
            $"late provider fault included secret-code and {Recipient("recipient")}"));
        await Task.Yield();

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus
                .DeliveryFailedCompensated,
            result.Status);
        Assert.Equal(1, store.CloseCalls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Compensation_timeout_returns_unknown()
    {
        var time = new ManualTimeProvider(Now);
        var compensationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = NewIssuedStore();
        store.CloseHandler = async (_, _, token) =>
        {
            compensationStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CloseUndeliveredTenantInvitationStatus.Closed;
        };
        var delivery = new StubDelivery
        {
            Handler = _ => throw new InvalidOperationException(
                "delivery failed"),
        };
        var logger = new RecordingLogger<TenantInvitationDeliveryService>();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            time,
            logger);

        var resultTask = IssueAsync(service);
        await compensationStarted.Task;
        time.Advance(
            TenantInvitationDeliveryService.CompensationTimeout);
        var result = await resultTask;

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus.DeliveryOutcomeUnknown,
            result.Status);
        Assert.Equal(
            TenantInvitationDeliveryService.DeliveryOutcomeUnknownEvent,
            Assert.Single(logger.Entries).EventId);
    }

    [Fact]
    public async Task Compensation_timeout_does_not_wait_for_a_non_cooperative_store()
    {
        var time = new ManualTimeProvider(Now);
        var compensationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource<
            CloseUndeliveredTenantInvitationStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var store = NewIssuedStore();
        store.CloseHandler = (_, _, _) =>
        {
            compensationStarted.SetResult();
            return neverCompletes.Task;
        };
        var delivery = new StubDelivery
        {
            Handler = _ => throw new InvalidOperationException(
                "delivery failed"),
        };
        var logger = new RecordingLogger<TenantInvitationDeliveryService>();
        var service = new TenantInvitationDeliveryService(
            store,
            delivery,
            time,
            logger);

        var resultTask = IssueAsync(service);
        await compensationStarted.Task;
        time.Advance(
            TenantInvitationDeliveryService.CompensationTimeout);
        var result = await resultTask;
        neverCompletes.SetException(new InvalidOperationException(
            $"late database fault included {Recipient("recipient")}"));
        await Task.Yield();

        Assert.Equal(
            IssueAndDeliverTenantInvitationStatus.DeliveryOutcomeUnknown,
            result.Status);
        Assert.Equal(
            TenantInvitationDeliveryService.DeliveryOutcomeUnknownEvent,
            Assert.Single(logger.Entries).EventId);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains(
                Recipient("recipient"),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Issuance_exception_is_rethrown_with_a_sanitized_event()
    {
        var sensitive = Recipient("recipient");
        var store = NewIssuedStore();
        store.IssueHandler = (_, _) => throw new InvalidOperationException(
            $"database message included {sensitive}");
        var logger = new RecordingLogger<TenantInvitationDeliveryService>();
        var service = new TenantInvitationDeliveryService(
            store,
            new StubDelivery(),
            new ManualTimeProvider(Now),
            logger);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => IssueAsync(service));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(
            TenantInvitationDeliveryService.IssuanceOutcomeUnknownEvent,
            entry.EventId);
        Assert.DoesNotContain(
            sensitive,
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(
            sensitive,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.InnerException);
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

    private static StubInvitationStore NewIssuedStore(
        Guid? invitationId = null) =>
        new(IssueTenantInvitationResult.Issued(
            invitationId ?? Guid.NewGuid(),
            UpperRecipient("recipient"),
            "secret-code",
            Now.AddHours(24)));

    private static Task<IssueAndDeliverTenantInvitationResult> IssueAsync(
        TenantInvitationDeliveryService service) =>
        service.IssueAndDeliverForOwnerAsync(
            "owner",
            "stamp",
            1,
            Guid.NewGuid(),
            Recipient("recipient"),
            default);

    private static string Recipient(string localPart) =>
        $"{localPart}@example.test";

    private static string UpperRecipient(string localPart) =>
        Recipient(localPart).ToUpperInvariant();

    private sealed class StubInvitationStore(
        IssueTenantInvitationResult result) : ITenantInvitationStore
    {
        public Func<
            string,
            CancellationToken,
            Task<IssueTenantInvitationResult>> IssueHandler
        {
            get;
            set;
        } = (_, _) => Task.FromResult(result);

        public Func<
            Guid,
            Guid,
            CancellationToken,
            Task<CloseUndeliveredTenantInvitationStatus>> CloseHandler
        {
            get;
            set;
        } = (_, _, _) => Task.FromResult(
            CloseUndeliveredTenantInvitationStatus.Closed);

        public int CloseCalls { get; private set; }

        public Task<IssueTenantInvitationResult> IssueForOwnerAsync(
            string userId,
            string securityStamp,
            long membershipVersion,
            Guid tenantId,
            string email,
            CancellationToken cancellationToken = default) =>
            IssueHandler(email, cancellationToken);

        public Task<CloseUndeliveredTenantInvitationStatus>
            CloseUndeliveredAsync(
                Guid tenantId,
                Guid invitationId,
                CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return CloseHandler(
                tenantId,
                invitationId,
                cancellationToken);
        }

        public Task<IssuedTenantInvitation> IssueAsync(
            Guid tenantId,
            string email,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PendingTenantInvitation>?>
            ListPendingForOwnerAsync(
                string userId,
                string securityStamp,
                long membershipVersion,
                Guid tenantId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CancelTenantInvitationResult> CancelForOwnerAsync(
            string userId,
            string securityStamp,
            long membershipVersion,
            Guid tenantId,
            Guid invitationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> AcceptAsync(
            string userId,
            string securityStamp,
            string code,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubDelivery : ITenantInvitationDelivery
    {
        public Func<CancellationToken, Task> Handler { get; set; } =
            _ => Task.CompletedTask;

        public int Calls { get; private set; }

        public string? RecipientEmail { get; private set; }

        public string? Code { get; private set; }

        public Task DeliverAsync(
            Guid invitationId,
            string recipientEmail,
            string code,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            Calls++;
            RecipientEmail = recipientEmail;
            Code = code;
            return Handler(cancellationToken);
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
