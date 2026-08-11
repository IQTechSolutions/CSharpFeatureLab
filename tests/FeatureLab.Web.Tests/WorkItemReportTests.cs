using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using FeatureLab.Features.BackgroundJobs;
using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class WorkItemReportTests : IClassFixture<FeatureLabWebFactory>
{
    private readonly FeatureLabWebFactory _factory;

    public WorkItemReportTests(FeatureLabWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_returns_accepted_then_the_job_completes_the_report_once()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var workItem = await CreateWorkItemAsync(client, "Prepare a release report");

        var response = await client.PostAsync(
            $"/api/work-items/{workItem.Id}/reports",
            content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted =
            await response.Content.ReadFromJsonAsync<WorkItemReportAcceptedResponse>();
        Assert.NotNull(accepted);
        Assert.Equal(
            $"/api/work-items/reports/{accepted.Report.Id}",
            response.Headers.Location?.ToString());
        Assert.Equal("Pending", accepted.Report.Status);
        Assert.Null(accepted.Report.Content);

        var scheduler =
            _factory.Services.GetRequiredService<RecordingWorkItemReportScheduler>();
        Assert.Equal(
            accepted.JobId,
            scheduler.EnqueuedReports[accepted.Report.Id].JobId);

        var pending = await client.GetFromJsonAsync<WorkItemReportResponse>(
            $"/api/work-items/reports/{accepted.Report.Id}");
        Assert.NotNull(pending);
        Assert.Equal("Pending", pending.Status);

        await RunReportJobAsync(accepted.Report.Id);

        var completed = await client.GetFromJsonAsync<WorkItemReportResponse>(
            $"/api/work-items/reports/{accepted.Report.Id}");
        Assert.NotNull(completed);
        Assert.Equal("Completed", completed.Status);
        Assert.Contains(
            "Prepare a release report",
            completed.Content,
            StringComparison.Ordinal);
        Assert.NotNull(completed.CompletedAtUtc);

        await RunReportJobAsync(accepted.Report.Id);

        var repeated = await client.GetFromJsonAsync<WorkItemReportResponse>(
            $"/api/work-items/reports/{accepted.Report.Id}");
        Assert.NotNull(repeated);
        Assert.Equal(completed.Content, repeated.Content);
        Assert.Equal(completed.CompletedAtUtc, repeated.CompletedAtUtc);
    }

    [Fact]
    public async Task Report_status_hides_another_users_report()
    {
        using var owner = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        using var otherUser = await CreateAuthenticatedClientAsync();
        var workItem = await CreateWorkItemAsync(owner, "Owner-only report");

        var request = await owner.PostAsync(
            $"/api/work-items/{workItem.Id}/reports",
            content: null);
        request.EnsureSuccessStatusCode();
        var accepted =
            await request.Content.ReadFromJsonAsync<WorkItemReportAcceptedResponse>();
        Assert.NotNull(accepted);

        var response = await otherUser.GetAsync(
            $"/api/work-items/reports/{accepted.Report.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Job_exposes_failures_for_three_bounded_retries()
    {
        var expected = new InvalidOperationException("Synthetic transient failure.");
        var job = new WorkItemReportJob(
            new TenantContext(),
            new FailingWorkItemReportService(expected));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                CancellationToken.None));
        Assert.Same(expected, actual);

        var retry = typeof(WorkItemReportJob)
            .GetMethod(nameof(WorkItemReportJob.RunAsync))
            ?.GetCustomAttribute<AutomaticRetryAttribute>();

        Assert.NotNull(retry);
        Assert.Equal(3, retry.Attempts);
        Assert.Equal(AttemptsExceededAction.Fail, retry.OnAttemptsExceeded);
    }

    [Fact]
    public async Task Job_rejects_a_report_from_another_tenant()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var workItem = await CreateWorkItemAsync(client, "Tenant-scoped report");
        var request = await client.PostAsync(
            $"/api/work-items/{workItem.Id}/reports",
            content: null);
        request.EnsureSuccessStatusCode();
        var accepted =
            await request.Content.ReadFromJsonAsync<WorkItemReportAcceptedResponse>();
        Assert.NotNull(accepted);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<WorkItemReportJob>();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => job.RunAsync(
                    Guid.NewGuid(),
                    accepted.Report.Id,
                    CancellationToken.None));
        }

        var report = await client.GetFromJsonAsync<WorkItemReportResponse>(
            $"/api/work-items/reports/{accepted.Report.Id}");
        Assert.NotNull(report);
        Assert.Equal("Pending", report.Status);
    }

    private async Task RunReportJobAsync(Guid reportId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<WorkItemReportJob>();
        var scheduler =
            _factory.Services.GetRequiredService<RecordingWorkItemReportScheduler>();
        var scheduledReport = scheduler.EnqueuedReports[reportId];
        await job.RunAsync(
            scheduledReport.TenantId,
            reportId,
            CancellationToken.None);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(
        bool canCreateWorkItems = false)
    {
        var client = _factory.CreateClient();
        var email = $"reporter-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";

        var registration = await client.PostAsJsonAsync("/account/register", new
        {
            email,
            password,
        });
        registration.EnsureSuccessStatusCode();

        await TenantTestData.ProvisionAsync(
            _factory.Services,
            email,
            Guid.NewGuid());

        if (canCreateWorkItems)
        {
            await GrantClaimAsync(
                email,
                new Claim(
                    WorkItemAuthorization.PermissionClaimType,
                    WorkItemAuthorization.CreatePermission));
        }

        var login = await client.PostAsJsonAsync("/account/login", new
        {
            email,
            password,
        });
        login.EnsureSuccessStatusCode();

        var tokens = await login.Content.ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static async Task<WorkItemResponse> CreateWorkItemAsync(
        HttpClient client,
        string title)
    {
        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title,
        });
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>();
        return Assert.IsType<WorkItemResponse>(created);
    }

    private async Task GrantClaimAsync(string email, Claim claim)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<FeatureLabUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        var result = await userManager.AddClaimAsync(user, claim);

        Assert.True(
            result.Succeeded,
            string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    private sealed class FailingWorkItemReportService(Exception failure)
        : IWorkItemReportService
    {
        public Task<WorkItemReport?> RequestAsync(
            Guid workItemId,
            string ownerId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkItemReport?> FindAsync(
            Guid reportId,
            string ownerId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task GenerateAsync(
            Guid reportId,
            CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }
}

public sealed class RecordingWorkItemReportScheduler : IWorkItemReportScheduler
{
    public ConcurrentDictionary<Guid, ScheduledReport> EnqueuedReports { get; } = new();

    public string Enqueue(Guid tenantId, Guid reportId)
    {
        var jobId = $"test-{reportId:N}";
        Assert.True(
            EnqueuedReports.TryAdd(
                reportId,
                new ScheduledReport(jobId, tenantId)));
        return jobId;
    }

    public sealed record ScheduledReport(
        string JobId,
        Guid TenantId);
}
