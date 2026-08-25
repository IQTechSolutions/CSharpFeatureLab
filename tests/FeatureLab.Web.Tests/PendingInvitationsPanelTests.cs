using AngleSharp.Dom;
using Bunit;
using FeatureLab.Client.Features.Tenancy;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class PendingInvitationsPanelTests : BunitContext
{
    [Fact]
    public void Initial_load_reports_loading_and_disables_refresh()
    {
        var loading = new TaskCompletionSource<LoadPendingInvitationsResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new StubTenantInvitationApi();
        api.EnqueueList(loading.Task);
        Services.AddSingleton<ITenantInvitationApi>(api);

        var panel = Render<PendingInvitationsPanel>();

        Assert.Contains("Loading pending invitations", panel.Markup);
        Assert.True(FindButton(panel, "Refresh").HasAttribute("disabled"));
    }

    [Fact]
    public void Initial_load_renders_email_and_utc_expiry_but_not_the_identifier()
    {
        var invitationId = Guid.Parse(
            "3c73eb25-2284-49c7-82f1-862f9478a3d2");
        var expiresAt = new DateTimeOffset(
            2026,
            8,
            26,
            16,
            30,
            0,
            TimeSpan.Zero);
        var api = new StubTenantInvitationApi();
        api.EnqueueList(
            new LoadPendingInvitationsResult.LoadedResult(
            [
                new PendingInvitationSummary(
                    invitationId,
                    "OWNER-CANDIDATE@EXAMPLE.TEST",
                    expiresAt),
            ]));
        Services.AddSingleton<ITenantInvitationApi>(api);

        var panel = Render<PendingInvitationsPanel>();

        panel.WaitForAssertion(() =>
        {
            Assert.Contains(
                "OWNER-CANDIDATE@EXAMPLE.TEST",
                panel.Markup,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                invitationId.ToString(),
                panel.Markup,
                StringComparison.OrdinalIgnoreCase);
            var expiry = panel.Find("time");
            Assert.Contains(
                "UTC",
                expiry.TextContent,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(DateTimeOffset.TryParse(
                expiry.GetAttribute("datetime"),
                out var renderedExpiry));
            Assert.Equal(expiresAt, renderedExpiry);
            Assert.Equal(1, api.ListCallCount);
        });
    }

    [Fact]
    public void Empty_server_snapshot_renders_an_explicit_empty_state()
    {
        var api = new StubTenantInvitationApi();
        api.EnqueueList(new LoadPendingInvitationsResult.LoadedResult([]));
        Services.AddSingleton<ITenantInvitationApi>(api);

        var panel = Render<PendingInvitationsPanel>();

        panel.WaitForAssertion(() =>
            Assert.Contains(
                "No open invitations are waiting in this workspace.",
                panel.Find("[data-testid=empty-state]").TextContent,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_uses_the_exact_identifier_disables_the_button_and_refreshes()
    {
        var invitationId = Guid.Parse(
            "3c73eb25-2284-49c7-82f1-862f9478a3d2");
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(invitationId, "PENDING@EXAMPLE.TEST"));
        api.EnqueueList(new LoadPendingInvitationsResult.LoadedResult([]));
        var cancellation = new TaskCompletionSource<CancelPendingInvitationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.EnqueueCancel(cancellation.Task);
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains("PENDING@EXAMPLE.TEST", panel.Markup));

        var click = FindCancelButton(panel).ClickAsync(
            new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(invitationId, Assert.Single(api.CancelledIds));
            Assert.True(FindCancelButton(panel).HasAttribute("disabled"));
            Assert.Equal(1, api.ListCallCount);
        });

        cancellation.SetResult(
            new CancelPendingInvitationResult.NoLongerPendingResult());
        await click;

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.DoesNotContain("PENDING@EXAMPLE.TEST", panel.Markup);
            Assert.Contains(
                "No pending invitation remains. The list was refreshed.",
                panel.Markup,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Manual_refresh_replaces_the_whole_server_snapshot()
    {
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(Guid.NewGuid(), "FIRST@EXAMPLE.TEST"));
        api.EnqueueList(Loaded(Guid.NewGuid(), "SECOND@EXAMPLE.TEST"));
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains("FIRST@EXAMPLE.TEST", panel.Markup));

        await FindButton(panel, "Refresh").ClickAsync(
            new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.DoesNotContain("FIRST@EXAMPLE.TEST", panel.Markup);
            Assert.Contains("SECOND@EXAMPLE.TEST", panel.Markup);
        });
    }

    [Theory]
    [InlineData(true, "sign in")]
    [InlineData(false, "owner")]
    public void Initial_load_explains_access_failures_safely(
        bool isUnauthorized,
        string expectedText)
    {
        var api = new StubTenantInvitationApi();
        api.EnqueueList(
            isUnauthorized
                ? new LoadPendingInvitationsResult.UnauthorizedResult()
                : new LoadPendingInvitationsResult.ForbiddenResult());
        Services.AddSingleton<ITenantInvitationApi>(api);

        var panel = Render<PendingInvitationsPanel>();

        panel.WaitForAssertion(() =>
            Assert.Contains(
                expectedText,
                panel.Find("[role=alert]").TextContent,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Initial_load_shows_a_generic_failure()
    {
        var api = new StubTenantInvitationApi();
        api.EnqueueList(new LoadPendingInvitationsResult.FailureResult());
        Services.AddSingleton<ITenantInvitationApi>(api);

        var panel = Render<PendingInvitationsPanel>();

        panel.WaitForAssertion(() =>
        {
            var message = panel.Find("[role=alert]").TextContent;
            Assert.Contains(
                "could not",
                message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "exception",
                message,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Cancel_failure_keeps_the_snapshot_and_allows_a_retry()
    {
        var invitationId = Guid.NewGuid();
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(invitationId, "STILL-PENDING@EXAMPLE.TEST"));
        api.EnqueueCancel(new CancelPendingInvitationResult.FailureResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains("STILL-PENDING@EXAMPLE.TEST", panel.Markup));

        await FindCancelButton(panel).ClickAsync(
            new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Contains("STILL-PENDING@EXAMPLE.TEST", panel.Markup);
            Assert.Contains(
                "could not",
                panel.Find("[role=alert]").TextContent,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(FindCancelButton(panel).HasAttribute("disabled"));
            Assert.Equal(1, api.ListCallCount);
        });
    }

    [Theory]
    [InlineData(true, "sign in")]
    [InlineData(false, "owner")]
    public async Task Cancel_access_failure_clears_the_private_snapshot(
        bool isUnauthorized,
        string expectedText)
    {
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(Guid.NewGuid(), "PRIVATE@EXAMPLE.TEST"));
        api.EnqueueCancel(
            isUnauthorized
                ? new CancelPendingInvitationResult.UnauthorizedResult()
                : new CancelPendingInvitationResult.ForbiddenResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains("PRIVATE@EXAMPLE.TEST", panel.Markup));

        await FindCancelButton(panel).ClickAsync(new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("PRIVATE@EXAMPLE.TEST", panel.Markup);
            Assert.Contains(
                expectedText,
                panel.Find("[role=alert]").TextContent,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Cancel_conflict_refreshes_without_claiming_success()
    {
        var invitationId = Guid.NewGuid();
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(invitationId, "OLD@EXAMPLE.TEST"));
        api.EnqueueList(Loaded(invitationId, "OLD@EXAMPLE.TEST"));
        api.EnqueueCancel(new CancelPendingInvitationResult.ConflictResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains("OLD@EXAMPLE.TEST", panel.Markup));

        await FindCancelButton(panel).ClickAsync(new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.Contains("OLD@EXAMPLE.TEST", panel.Markup);
            Assert.Contains(
                "could not be updated",
                panel.Find("[role=alert]").TextContent,
                StringComparison.Ordinal);
            Assert.Empty(panel.FindAll(".success"));
        });
    }

    [Fact]
    public async Task Successful_command_does_not_claim_a_refresh_that_failed()
    {
        var invitationId = Guid.NewGuid();
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(invitationId, "STALE@EXAMPLE.TEST"));
        api.EnqueueList(new LoadPendingInvitationsResult.FailureResult());
        api.EnqueueCancel(
            new CancelPendingInvitationResult.NoLongerPendingResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains("STALE@EXAMPLE.TEST", panel.Markup));

        await FindCancelButton(panel).ClickAsync(
            new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.Contains("STALE@EXAMPLE.TEST", panel.Markup);
            Assert.DoesNotContain(
                "No pending invitation remains. The list was refreshed.",
                panel.Markup,
                StringComparison.Ordinal);
            Assert.Contains(
                "could not",
                panel.Find("[role=alert]").TextContent,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static LoadPendingInvitationsResult.LoadedResult Loaded(
        Guid invitationId,
        string email) =>
        new(
        [
            new PendingInvitationSummary(
                invitationId,
                email,
                new DateTimeOffset(
                    2026,
                    8,
                    26,
                    16,
                    30,
                    0,
                    TimeSpan.Zero)),
        ]);

    private static IElement FindButton(
        IRenderedComponent<PendingInvitationsPanel> panel,
        string text) =>
        panel.FindAll("button").Single(button =>
            button.TextContent.Contains(
                text,
                StringComparison.OrdinalIgnoreCase));

    private static IElement FindCancelButton(
        IRenderedComponent<PendingInvitationsPanel> panel) =>
        panel.Find("button[aria-label^='Cancel invitation for']");

    private sealed class StubTenantInvitationApi : ITenantInvitationApi
    {
        private readonly Queue<Task<LoadPendingInvitationsResult>> _listResults =
            new();
        private readonly Queue<Task<CancelPendingInvitationResult>> _cancelResults =
            new();

        public int ListCallCount { get; private set; }

        public List<Guid> CancelledIds { get; } = [];

        public void EnqueueList(LoadPendingInvitationsResult result) =>
            EnqueueList(Task.FromResult(result));

        public void EnqueueList(Task<LoadPendingInvitationsResult> result) =>
            _listResults.Enqueue(result);

        public void EnqueueCancel(CancelPendingInvitationResult result) =>
            EnqueueCancel(Task.FromResult(result));

        public void EnqueueCancel(Task<CancelPendingInvitationResult> result) =>
            _cancelResults.Enqueue(result);

        public Task<LoadPendingInvitationsResult> ListPendingAsync(
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            return _listResults.Dequeue();
        }

        public Task<CancelPendingInvitationResult> CancelAsync(
            Guid invitationId,
            CancellationToken cancellationToken = default)
        {
            CancelledIds.Add(invitationId);
            return _cancelResults.Dequeue();
        }
    }
}
