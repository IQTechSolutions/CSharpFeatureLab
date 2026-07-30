using FeatureLab.Data;
using FeatureLab.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Features.WorkItems;

public interface IWorkItemStore
{
    Task<WorkItem> AddAsync(WorkItem workItem, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItem>> ListAsync(string ownerId, CancellationToken cancellationToken);

    Task<UpdateWorkItemResult> UpdateTitleAsync(
        Guid id,
        string ownerId,
        string title,
        int expectedVersion,
        CancellationToken cancellationToken);
}

public sealed class EfWorkItemStore(
    FeatureLabDbContext dbContext,
    ITenantContext tenant) : IWorkItemStore
{
    public async Task<WorkItem> AddAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (workItem.TenantId != tenant.Id)
        {
            throw new InvalidOperationException(
                "The work item belongs to a different tenant scope.");
        }

        dbContext.WorkItems.Add(workItem);
        await dbContext.SaveChangesAsync(cancellationToken);
        return workItem;
    }

    public async Task<IReadOnlyList<WorkItem>> ListAsync(string ownerId, CancellationToken cancellationToken) =>
        await dbContext.WorkItems
            .AsNoTracking()
            .Where(item => item.OwnerId == ownerId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

    public async Task<UpdateWorkItemResult> UpdateTitleAsync(
        Guid id,
        string ownerId,
        string title,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var workItem = await dbContext.WorkItems.SingleOrDefaultAsync(
            item => item.Id == id && item.OwnerId == ownerId,
            cancellationToken);

        if (workItem is null)
        {
            return UpdateWorkItemResult.NotFound();
        }

        dbContext.Entry(workItem)
            .Property(item => item.Version)
            .OriginalValue = expectedVersion;
        workItem.Rename(title);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return UpdateWorkItemResult.Updated(workItem);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.Entry(workItem).State = EntityState.Detached;
            return UpdateWorkItemResult.Conflict();
        }
    }
}

public abstract record UpdateWorkItemResult
{
    private UpdateWorkItemResult()
    {
    }

    public sealed record UpdatedResult(WorkItem WorkItem) : UpdateWorkItemResult;

    public sealed record NotFoundResult : UpdateWorkItemResult;

    public sealed record ConflictResult : UpdateWorkItemResult;

    public static UpdateWorkItemResult Updated(WorkItem workItem) =>
        new UpdatedResult(workItem);

    public static UpdateWorkItemResult NotFound() =>
        new NotFoundResult();

    public static UpdateWorkItemResult Conflict() =>
        new ConflictResult();
}
