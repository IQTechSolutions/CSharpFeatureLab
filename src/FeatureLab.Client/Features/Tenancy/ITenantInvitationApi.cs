namespace FeatureLab.Client.Features.Tenancy;

public interface ITenantInvitationApi
{
    Task<IssuePendingInvitationResult> IssueAsync(
        string recipientEmail,
        CancellationToken cancellationToken = default);

    Task<LoadPendingInvitationsResult> ListPendingAsync(
        CancellationToken cancellationToken = default);

    Task<CancelPendingInvitationResult> CancelAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default);
}

public sealed record PendingInvitationSummary(
    Guid Id,
    string Email,
    DateTimeOffset ExpiresAt);

public abstract record IssuePendingInvitationResult
{
    private IssuePendingInvitationResult()
    {
    }

    public sealed record IssuedResult(
        string Code,
        DateTimeOffset ExpiresAt)
        : IssuePendingInvitationResult;

    public sealed record InvalidRecipientResult
        : IssuePendingInvitationResult;

    public sealed record UnauthorizedResult
        : IssuePendingInvitationResult;

    public sealed record ForbiddenResult
        : IssuePendingInvitationResult;

    public sealed record ConflictResult
        : IssuePendingInvitationResult;

    public sealed record FailureResult
        : IssuePendingInvitationResult;

    public static IssuePendingInvitationResult Issued(
        string code,
        DateTimeOffset expiresAt) =>
        new IssuedResult(code, expiresAt);

    public static IssuePendingInvitationResult InvalidRecipient() =>
        new InvalidRecipientResult();

    public static IssuePendingInvitationResult Unauthorized() =>
        new UnauthorizedResult();

    public static IssuePendingInvitationResult Forbidden() =>
        new ForbiddenResult();

    public static IssuePendingInvitationResult Conflict() =>
        new ConflictResult();

    public static IssuePendingInvitationResult Failure() =>
        new FailureResult();
}

public abstract record LoadPendingInvitationsResult
{
    private LoadPendingInvitationsResult()
    {
    }

    public sealed record LoadedResult(
        IReadOnlyList<PendingInvitationSummary> Invitations)
        : LoadPendingInvitationsResult;

    public sealed record UnauthorizedResult
        : LoadPendingInvitationsResult;

    public sealed record ForbiddenResult
        : LoadPendingInvitationsResult;

    public sealed record FailureResult
        : LoadPendingInvitationsResult;

    public static LoadPendingInvitationsResult Loaded(
        IReadOnlyList<PendingInvitationSummary> invitations) =>
        new LoadedResult(invitations);

    public static LoadPendingInvitationsResult Unauthorized() =>
        new UnauthorizedResult();

    public static LoadPendingInvitationsResult Forbidden() =>
        new ForbiddenResult();

    public static LoadPendingInvitationsResult Failure() =>
        new FailureResult();
}

public abstract record CancelPendingInvitationResult
{
    private CancelPendingInvitationResult()
    {
    }

    public sealed record NoLongerPendingResult
        : CancelPendingInvitationResult;

    public sealed record UnauthorizedResult
        : CancelPendingInvitationResult;

    public sealed record ForbiddenResult
        : CancelPendingInvitationResult;

    public sealed record ConflictResult
        : CancelPendingInvitationResult;

    public sealed record FailureResult
        : CancelPendingInvitationResult;

    public static CancelPendingInvitationResult NoLongerPending() =>
        new NoLongerPendingResult();

    public static CancelPendingInvitationResult Unauthorized() =>
        new UnauthorizedResult();

    public static CancelPendingInvitationResult Forbidden() =>
        new ForbiddenResult();

    public static CancelPendingInvitationResult Conflict() =>
        new ConflictResult();

    public static CancelPendingInvitationResult Failure() =>
        new FailureResult();
}
