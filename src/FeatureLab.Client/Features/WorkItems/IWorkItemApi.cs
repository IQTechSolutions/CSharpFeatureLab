namespace FeatureLab.Client.Features.WorkItems;

public interface IWorkItemApi
{
    Task<CreateWorkItemResult> CreateAsync(
        string title,
        CancellationToken cancellationToken = default);
}

public abstract record CreateWorkItemResult
{
    private CreateWorkItemResult()
    {
    }

    public sealed record CreatedResult(Guid Id, string Title) : CreateWorkItemResult;

    public sealed record ValidationResult(
        IReadOnlyDictionary<string, string[]> Errors) : CreateWorkItemResult;

    public sealed record ForbiddenResult : CreateWorkItemResult;

    public sealed record UnauthorizedResult : CreateWorkItemResult;

    public sealed record FailureResult : CreateWorkItemResult;

    public static CreateWorkItemResult Created(Guid id, string title) =>
        new CreatedResult(id, title);

    public static CreateWorkItemResult Validation(
        IReadOnlyDictionary<string, string[]> errors) =>
        new ValidationResult(errors);

    public static CreateWorkItemResult Forbidden() =>
        new ForbiddenResult();

    public static CreateWorkItemResult Unauthorized() =>
        new UnauthorizedResult();

    public static CreateWorkItemResult Failure() =>
        new FailureResult();
}
