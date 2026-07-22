using System.Collections.Concurrent;

namespace FeatureLab.Features.WorkItems;

public interface IWorkItemStore
{
    WorkItem Add(WorkItem workItem);

    IReadOnlyList<WorkItem> List();
}

public sealed class InMemoryWorkItemStore : IWorkItemStore
{
    private readonly ConcurrentDictionary<Guid, WorkItem> _items = new();

    public WorkItem Add(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (!_items.TryAdd(workItem.Id, workItem))
        {
            throw new InvalidOperationException($"Work item '{workItem.Id}' already exists.");
        }

        return workItem;
    }

    public IReadOnlyList<WorkItem> List() =>
        _items.Values
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToArray();
}

