using Hangfire;
using FeatureLab.Tenancy;

namespace FeatureLab.Features.BackgroundJobs;

public sealed class WorkItemReportJob(
    TenantContext tenant,
    IWorkItemReportService reports)
{
    [AutomaticRetry(
        Attempts = 3,
        DelaysInSeconds = new[] { 5, 15, 30 },
        OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RunAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken)
    {
        tenant.Set(tenantId);
        return reports.GenerateAsync(reportId, cancellationToken);
    }
}

public interface IWorkItemReportScheduler
{
    string Enqueue(Guid tenantId, Guid reportId);
}

public sealed class HangfireWorkItemReportScheduler(
    IBackgroundJobClient backgroundJobs) : IWorkItemReportScheduler
{
    public string Enqueue(Guid tenantId, Guid reportId) =>
        backgroundJobs.Enqueue<WorkItemReportJob>(
            job => job.RunAsync(
                tenantId,
                reportId,
                CancellationToken.None));
}
