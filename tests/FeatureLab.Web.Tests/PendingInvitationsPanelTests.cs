using AngleSharp.Dom;
using Bunit;
using FeatureLab.Client.Features.Tenancy;
using Microsoft.AspNetCore.Components;
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
        var recipient = Recipient("owner-candidate");
        var api = new StubTenantInvitationApi();
        api.EnqueueList(
            new LoadPendingInvitationsResult.LoadedResult(
            [
                new PendingInvitationSummary(
                    invitationId,
                    recipient,
                    expiresAt),
            ]));
        Services.AddSingleton<ITenantInvitationApi>(api);

        var panel = Render<PendingInvitationsPanel>();

        panel.WaitForAssertion(() =>
        {
            Assert.Contains(
                recipient,
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
    public async Task Issue_reveals_the_raw_code_once_and_refreshes_the_safe_list()
    {
        const string code = "server-issued-acceptance-code";
        var recipient = Recipient("invited-member");
        var invitationId = Guid.NewGuid();
        var expiresAt = new DateTimeOffset(
            2026,
            9,
            2,
            16,
            30,
            0,
            TimeSpan.Zero);
        var api = new StubTenantInvitationApi();
        api.EnqueueList(new LoadPendingInvitationsResult.LoadedResult([]));
        api.EnqueueList(Loaded(invitationId, recipient));
        api.EnqueueIssue(
            new IssuePendingInvitationResult.IssuedResult(
                code,
                expiresAt));
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() => Assert.Equal(1, api.ListCallCount));

        await panel.Find("#recipient-email").ChangeAsync(
            new ChangeEventArgs { Value = $"  {recipient}  " });
        await panel.Find("form").SubmitAsync();

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(1, api.IssueCallCount);
            Assert.Equal($"  {recipient}  ", Assert.Single(api.IssuedRecipients));
            Assert.Equal(2, api.ListCallCount);
            var reveal = panel.Find("[data-testid=issued-code]");
            Assert.Equal(
                code,
                reveal.QuerySelector("input")?.GetAttribute("value"));
            Assert.True(
                reveal.QuerySelector("input")?.HasAttribute("readonly"));
            Assert.Equal(1, Occurrences(panel.Markup, code));
            Assert.Contains(recipient, panel.Find("ul").TextContent);
            Assert.DoesNotContain(code, panel.Find("ul").TextContent);
            Assert.Equal(
                string.Empty,
                panel.Find("#recipient-email").GetAttribute("value"));
            Assert.Null(panel.Find("form").GetAttribute("action"));
        });
    }

    [Fact]
    public async Task Manual_refresh_clears_the_code_and_cannot_restore_it_from_the_list()
    {
        const string code = "one-display-only-code";
        var recipient = Recipient("invited-member");
        var invitationId = Guid.NewGuid();
        var api = new StubTenantInvitationApi();
        api.EnqueueList(new LoadPendingInvitationsResult.LoadedResult([]));
        api.EnqueueList(Loaded(invitationId, recipient));
        api.EnqueueList(Loaded(invitationId, recipient));
        api.EnqueueIssue(
            IssuePendingInvitationResult.Issued(
                code,
                new DateTimeOffset(
                    2026,
                    9,
                    2,
                    16,
                    30,
                    0,
                    TimeSpan.Zero)));
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() => Assert.Equal(1, api.ListCallCount));

        await panel.Find("#recipient-email").ChangeAsync(
            new ChangeEventArgs { Value = recipient });
        await panel.Find("form").SubmitAsync();
        panel.WaitForAssertion(() => Assert.Contains(code, panel.Markup));

        await FindButton(panel, "Refresh").ClickAsync(new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(3, api.ListCallCount);
            Assert.Equal(1, api.IssueCallCount);
            Assert.DoesNotContain(code, panel.Markup);
            Assert.Empty(panel.FindAll("[data-testid=issued-code]"));
            Assert.Contains(recipient, panel.Find("ul").TextContent);
        });
    }

    [Fact]
    public async Task New_issue_clears_the_previous_code_and_blocks_duplicates()
    {
        const string firstCode = "first-one-time-code";
        var firstRecipient = Recipient("first-member");
        var secondRecipient = Recipient("second-member");
        var api = new StubTenantInvitationApi();
        api.EnqueueList(new LoadPendingInvitationsResult.LoadedResult([]));
        api.EnqueueList(Loaded(Guid.NewGuid(), firstRecipient));
        api.EnqueueIssue(
            IssuePendingInvitationResult.Issued(
                firstCode,
                DateTimeOffset.UtcNow.AddDays(7)));
        var pendingIssue =
            new TaskCompletionSource<IssuePendingInvitationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        api.EnqueueIssue(pendingIssue.Task);
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() => Assert.Equal(1, api.ListCallCount));
        await panel.Find("#recipient-email").ChangeAsync(
            new ChangeEventArgs { Value = firstRecipient });
        await panel.Find("form").SubmitAsync();
        panel.WaitForAssertion(() => Assert.Contains(firstCode, panel.Markup));
        await panel.Find("#recipient-email").ChangeAsync(
            new ChangeEventArgs { Value = secondRecipient });

        var firstSubmit = panel.Find("form").SubmitAsync();
        await panel.Find("form").SubmitAsync();

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.IssueCallCount);
            Assert.DoesNotContain(firstCode, panel.Markup);
            Assert.True(
                FindButton(panel, "Issuing").HasAttribute("disabled"));
        });

        pendingIssue.SetResult(IssuePendingInvitationResult.Failure());
        await firstSubmit;
    }

    [Theory]
    [InlineData(true, "sign in")]
    [InlineData(false, "owner")]
    public async Task Issue_access_failure_clears_the_private_snapshot(
        bool isUnauthorized,
        string expectedText)
    {
        var privateRecipient = Recipient("private");
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(Guid.NewGuid(), privateRecipient));
        api.EnqueueIssue(
            isUnauthorized
                ? IssuePendingInvitationResult.Unauthorized()
                : IssuePendingInvitationResult.Forbidden());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(privateRecipient, panel.Markup));

        await panel.Find("#recipient-email").ChangeAsync(
            new ChangeEventArgs { Value = Recipient("new-member") });
        await panel.Find("form").SubmitAsync();

        panel.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(privateRecipient, panel.Markup);
            Assert.Contains(
                expectedText,
                panel.Find("[role=alert]").TextContent,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(panel.FindAll("[data-testid=issued-code]"));
        });
    }

    [Theory]
    [InlineData(true, "cannot be issued")]
    [InlineData(false, "could not issue")]
    public async Task Issue_conflict_and_failure_keep_safe_generic_messages(
        bool isConflict,
        string expectedText)
    {
        var existingRecipient = Recipient("existing");
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(Guid.NewGuid(), existingRecipient));
        api.EnqueueIssue(
            isConflict
                ? IssuePendingInvitationResult.Conflict()
                : IssuePendingInvitationResult.Failure());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(existingRecipient, panel.Markup));

        await panel.Find("#recipient-email").ChangeAsync(
            new ChangeEventArgs { Value = Recipient("new-member") });
        await panel.Find("form").SubmitAsync();

        panel.WaitForAssertion(() =>
        {
            Assert.Contains(existingRecipient, panel.Markup);
            var message = panel.Find("[role=alert]").TextContent;
            Assert.Contains(
                expectedText,
                message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "exception",
                message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(panel.FindAll("[data-testid=issued-code]"));
        });
    }

    [Fact]
    public async Task Cancel_uses_the_exact_identifier_disables_the_button_and_refreshes()
    {
        var invitationId = Guid.Parse(
            "3c73eb25-2284-49c7-82f1-862f9478a3d2");
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(invitationId, Recipient("pending")));
        api.EnqueueList(new LoadPendingInvitationsResult.LoadedResult([]));
        var cancellation = new TaskCompletionSource<CancelPendingInvitationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.EnqueueCancel(cancellation.Task);
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(Recipient("pending"), panel.Markup));

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
            Assert.DoesNotContain(Recipient("pending"), panel.Markup);
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
        api.EnqueueList(Loaded(Guid.NewGuid(), Recipient("first")));
        api.EnqueueList(Loaded(Guid.NewGuid(), Recipient("second")));
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(Recipient("first"), panel.Markup));

        await FindButton(panel, "Refresh").ClickAsync(
            new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.DoesNotContain(Recipient("first"), panel.Markup);
            Assert.Contains(Recipient("second"), panel.Markup);
        });
    }

    [Fact]
    public async Task Failed_manual_refresh_keeps_the_snapshot_and_labels_it_stale()
    {
        var api = new StubTenantInvitationApi();
        api.EnqueueList(Loaded(Guid.NewGuid(), Recipient("existing")));
        api.EnqueueList(new LoadPendingInvitationsResult.FailureResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(Recipient("existing"), panel.Markup));

        await FindButton(panel, "Refresh").ClickAsync(new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.Contains(Recipient("existing"), panel.Markup);
            Assert.Contains(
                "may be out of date",
                panel.Find("[role=alert]").TextContent,
                StringComparison.OrdinalIgnoreCase);
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
        api.EnqueueList(Loaded(invitationId, Recipient("still-pending")));
        api.EnqueueCancel(new CancelPendingInvitationResult.FailureResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(Recipient("still-pending"), panel.Markup));

        await FindCancelButton(panel).ClickAsync(
            new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Contains(Recipient("still-pending"), panel.Markup);
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
        api.EnqueueList(Loaded(Guid.NewGuid(), Recipient("private")));
        api.EnqueueCancel(
            isUnauthorized
                ? new CancelPendingInvitationResult.UnauthorizedResult()
                : new CancelPendingInvitationResult.ForbiddenResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(Recipient("private"), panel.Markup));

        await FindCancelButton(panel).ClickAsync(new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(Recipient("private"), panel.Markup);
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
        api.EnqueueList(Loaded(invitationId, Recipient("old")));
        api.EnqueueList(Loaded(invitationId, Recipient("old")));
        api.EnqueueCancel(new CancelPendingInvitationResult.ConflictResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(Recipient("old"), panel.Markup));

        await FindCancelButton(panel).ClickAsync(new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.Contains(Recipient("old"), panel.Markup);
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
        api.EnqueueList(Loaded(invitationId, Recipient("stale")));
        api.EnqueueList(new LoadPendingInvitationsResult.FailureResult());
        api.EnqueueCancel(
            new CancelPendingInvitationResult.NoLongerPendingResult());
        Services.AddSingleton<ITenantInvitationApi>(api);
        var panel = Render<PendingInvitationsPanel>();
        panel.WaitForAssertion(() =>
            Assert.Contains(Recipient("stale"), panel.Markup));

        await FindCancelButton(panel).ClickAsync(
            new MouseEventArgs());

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.ListCallCount);
            Assert.Contains(Recipient("stale"), panel.Markup);
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

    private static string Recipient(string localPart) =>
        $"{localPart}@example.test";

    private static int Occurrences(string value, string search) =>
        value.Split(search, StringSplitOptions.None).Length - 1;

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
        private readonly Queue<Task<IssuePendingInvitationResult>> _issueResults =
            new();
        private readonly Queue<Task<LoadPendingInvitationsResult>> _listResults =
            new();
        private readonly Queue<Task<CancelPendingInvitationResult>> _cancelResults =
            new();

        public int IssueCallCount { get; private set; }

        public int ListCallCount { get; private set; }

        public List<string> IssuedRecipients { get; } = [];

        public List<Guid> CancelledIds { get; } = [];

        public void EnqueueIssue(IssuePendingInvitationResult result) =>
            EnqueueIssue(Task.FromResult(result));

        public void EnqueueIssue(Task<IssuePendingInvitationResult> result) =>
            _issueResults.Enqueue(result);

        public void EnqueueList(LoadPendingInvitationsResult result) =>
            EnqueueList(Task.FromResult(result));

        public void EnqueueList(Task<LoadPendingInvitationsResult> result) =>
            _listResults.Enqueue(result);

        public void EnqueueCancel(CancelPendingInvitationResult result) =>
            EnqueueCancel(Task.FromResult(result));

        public void EnqueueCancel(Task<CancelPendingInvitationResult> result) =>
            _cancelResults.Enqueue(result);

        public Task<IssuePendingInvitationResult> IssueAsync(
            string recipientEmail,
            CancellationToken cancellationToken = default)
        {
            IssueCallCount++;
            IssuedRecipients.Add(recipientEmail);
            return _issueResults.Dequeue();
        }

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
