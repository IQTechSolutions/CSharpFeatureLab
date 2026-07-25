using System.Security.Claims;

namespace FeatureLab.Features.WorkItems;

public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/work-items")
            .RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, IWorkItemStore store, CancellationToken cancellationToken) =>
        {
            var ownerId = RequiredOwnerId(user);
            var workItems = await store.ListAsync(ownerId, cancellationToken);
            return Results.Ok(workItems.Select(WorkItemResponse.From));
        });

        group.MapPost(
            "/",
            async (
                CreateWorkItemRequest request,
                ClaimsPrincipal user,
                IWorkItemStore store,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var workItem = WorkItem.Create(request.Title, RequiredOwnerId(user), TimeProvider.System);
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
            })
            .RequireAuthorization(WorkItemAuthorization.CreatePolicy);

        return endpoints;
    }

    private static string RequiredOwnerId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The authenticated user has no name identifier claim.");
}

public sealed record CreateWorkItemRequest(string Title);

public sealed record WorkItemResponse(Guid Id, string Title, bool IsCompleted, DateTime CreatedAtUtc)
{
    public static WorkItemResponse From(WorkItem workItem) =>
        new(workItem.Id, workItem.Title, workItem.IsCompleted, workItem.CreatedAtUtc);
}
