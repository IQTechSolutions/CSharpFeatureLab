using Hangfire;

namespace FeatureLab.Features.BackgroundJobs;

public sealed class WorkItemReportJob(IWorkItemReportService reports)
{
    [AutomaticRetry(
        Attempts = 3,
        DelaysInSeconds = new[] { 5, 15, 30 },
        OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RunAsync(
        Guid reportId,
        CancellationToken cancellationToken) =>
        reports.GenerateAsync(reportId, cancellationToken);
}

public interface IWorkItemReportScheduler
{
    string Enqueue(Guid reportId);
}

public sealed class HangfireWorkItemReportScheduler(
    IBackgroundJobClient backgroundJobs) : IWorkItemReportScheduler
{
    public string Enqueue(Guid reportId) =>
        backgroundJobs.Enqueue<WorkItemReportJob>(
            job => job.RunAsync(reportId, CancellationToken.None));
}
