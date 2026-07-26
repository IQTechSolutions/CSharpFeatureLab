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

        group.MapPut(
            "/{id:guid}/title",
            async (
                Guid id,
                UpdateWorkItemTitleRequest request,
                ClaimsPrincipal user,
                IWorkItemStore store,
                CancellationToken cancellationToken) =>
            {
                if (request.Version < 1)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        [nameof(request.Version)] = ["Version must be 1 or greater."],
                    });
                }

                try
                {
                    var result = await store.UpdateTitleAsync(
                        id,
                        RequiredOwnerId(user),
                        request.Title,
                        request.Version,
                        cancellationToken);

                    return result switch
                    {
                        UpdateWorkItemResult.UpdatedResult updated =>
                            Results.Ok(WorkItemResponse.From(updated.WorkItem)),
                        UpdateWorkItemResult.NotFoundResult =>
                            Results.NotFound(),
                        UpdateWorkItemResult.ConflictResult =>
                            Results.Problem(
                                statusCode: StatusCodes.Status409Conflict,
                                title: "The work item changed before this update was saved.",
                                detail: "Reload the work item and try your change again."),
                        _ => throw new InvalidOperationException(
                            "The work item update returned an unknown result."),
                    };
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

    private static string RequiredOwnerId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The authenticated user has no name identifier claim.");
}

public sealed record CreateWorkItemRequest(string Title);

public sealed record UpdateWorkItemTitleRequest(string Title, int Version);

public sealed record WorkItemResponse(
    Guid Id,
    string Title,
    bool IsCompleted,
    DateTime CreatedAtUtc,
    int Version)
{
    public static WorkItemResponse From(WorkItem workItem) =>
        new(
            workItem.Id,
            workItem.Title,
            workItem.IsCompleted,
            workItem.CreatedAtUtc,
            workItem.Version);
}
