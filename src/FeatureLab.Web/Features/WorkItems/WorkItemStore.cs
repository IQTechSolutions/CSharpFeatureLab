using FeatureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Features.WorkItems;

public interface IWorkItemStore
{
    Task<WorkItem> AddAsync(WorkItem workItem, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItem>> ListAsync(CancellationToken cancellationToken);
}

public sealed class EfWorkItemStore(FeatureLabDbContext dbContext) : IWorkItemStore
{
    public async Task<WorkItem> AddAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        dbContext.WorkItems.Add(workItem);
        await dbContext.SaveChangesAsync(cancellationToken);
        return workItem;
    }

    public async Task<IReadOnlyList<WorkItem>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.WorkItems
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);
}
