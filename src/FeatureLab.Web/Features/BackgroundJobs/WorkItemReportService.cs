using FeatureLab.Data;
using FeatureLab.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Features.BackgroundJobs;

public interface IWorkItemReportService
{
    Task<WorkItemReport?> RequestAsync(
        Guid workItemId,
        string ownerId,
        CancellationToken cancellationToken);

    Task<WorkItemReport?> FindAsync(
        Guid reportId,
        string ownerId,
        CancellationToken cancellationToken);

    Task GenerateAsync(
        Guid reportId,
        CancellationToken cancellationToken);
}

public sealed class EfWorkItemReportService(
    FeatureLabDbContext dbContext,
    ITenantContext tenant,
    TimeProvider timeProvider) : IWorkItemReportService
{
    public async Task<WorkItemReport?> RequestAsync(
        Guid workItemId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var ownsWorkItem = await dbContext.WorkItems
            .AsNoTracking()
            .AnyAsync(
                item => item.Id == workItemId && item.OwnerId == ownerId,
                cancellationToken);

        if (!ownsWorkItem)
        {
            return null;
        }

        var report = WorkItemReport.Request(
            workItemId,
            ownerId,
            tenant.Id,
            timeProvider);
        dbContext.WorkItemReports.Add(report);
        await dbContext.SaveChangesAsync(cancellationToken);
        return report;
    }

    public Task<WorkItemReport?> FindAsync(
        Guid reportId,
        string ownerId,
        CancellationToken cancellationToken) =>
        dbContext.WorkItemReports
            .AsNoTracking()
            .SingleOrDefaultAsync(
                report => report.Id == reportId && report.OwnerId == ownerId,
                cancellationToken);

    public async Task GenerateAsync(
        Guid reportId,
        CancellationToken cancellationToken)
    {
        var report = await dbContext.WorkItemReports
            .SingleOrDefaultAsync(
                candidate => candidate.Id == reportId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Work item report '{reportId}' was not found.");

        if (report.CompletedAtUtc is not null)
        {
            return;
        }

        var workItem = await dbContext.WorkItems
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == report.WorkItemId
                    && item.OwnerId == report.OwnerId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Work item '{report.WorkItemId}' was not found for this report.");

        var content =
            $"Review \"{workItem.Title}\": "
            + $"created {workItem.CreatedAtUtc:yyyy-MM-dd}; "
            + $"status {(workItem.IsCompleted ? "completed" : "open")}.";

        report.Complete(content, timeProvider);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
