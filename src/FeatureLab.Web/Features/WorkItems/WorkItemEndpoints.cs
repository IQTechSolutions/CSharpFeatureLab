namespace FeatureLab.Features.WorkItems;

public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/work-items");

        group.MapGet("/", async (IWorkItemStore store, CancellationToken cancellationToken) =>
        {
            var workItems = await store.ListAsync(cancellationToken);
            return Results.Ok(workItems.Select(WorkItemResponse.From));
        });

        group.MapPost("/", async (CreateWorkItemRequest request, IWorkItemStore store, CancellationToken cancellationToken) =>
        {
            try
            {
                var workItem = WorkItem.Create(request.Title, TimeProvider.System);
                await store.AddAsync(workItem, cancellationToken);

                return Results.Created($"/api/work-items/{workItem.Id}", WorkItemResponse.From(workItem));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Title)] = [exception.Message],
                });
            }
        });

        return endpoints;
    }
}

public sealed record CreateWorkItemRequest(string Title);

public sealed record WorkItemResponse(Guid Id, string Title, bool IsCompleted, DateTime CreatedAtUtc)
{
    public static WorkItemResponse From(WorkItem workItem) =>
        new(workItem.Id, workItem.Title, workItem.IsCompleted, workItem.CreatedAtUtc);
}
