using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FeatureLab.Tenancy;

public static class TenantInvitationEndpoints
{
    public static IEndpointRouteBuilder MapTenantInvitationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/tenant-invitations",
                async (
                    ClaimsPrincipal principal,
                    HttpContext httpContext,
                    ITenantContext tenant,
                    ITenantInvitationStore invitations,
                    IOptions<IdentityOptions> identityOptions,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    var securityStamps = principal.FindAll(
                            identityOptions.Value.ClaimsIdentity
                                .SecurityStampClaimType)
                        .Select(claim => claim.Value)
                        .ToArray();
                    if (securityStamps.Length != 1
                        || string.IsNullOrWhiteSpace(securityStamps[0])
                        || !TenantMembership.TryGetVersion(
                            principal,
                            out var membershipVersion))
                    {
                        return Results.Forbid();
                    }

                    var pending =
                        await invitations.ListPendingForOwnerAsync(
                            userId,
                            securityStamps[0],
                            membershipVersion,
                            tenant.Id,
                            cancellationToken);
                    if (pending is null)
                    {
                        return Results.Forbid();
                    }

                    httpContext.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(pending);
                })
            .RequireAuthorization(TenantMembership.OwnerPolicy);

        endpoints.MapPost(
                "/api/tenant-invitations",
                async (
                    IssueTenantInvitationRequest request,
                    ClaimsPrincipal principal,
                    HttpContext httpContext,
                    ITenantContext tenant,
                    ITenantInvitationStore invitations,
                    IOptions<IdentityOptions> identityOptions,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    var securityStamps = principal.FindAll(
                            identityOptions.Value.ClaimsIdentity
                                .SecurityStampClaimType)
                        .Select(claim => claim.Value)
                        .ToArray();
                    if (securityStamps.Length != 1
                        || string.IsNullOrWhiteSpace(securityStamps[0])
                        || !TenantMembership.TryGetVersion(
                            principal,
                            out var membershipVersion))
                    {
                        return Results.Forbid();
                    }

                    var result = await invitations.IssueForOwnerAsync(
                            userId,
                            securityStamps[0],
                            membershipVersion,
                            tenant.Id,
                            request.Email ?? string.Empty,
                            cancellationToken);

                    return result.Status switch
                    {
                        IssueTenantInvitationStatus.Queued
                            => QueuedInvitation(httpContext, result),
                        IssueTenantInvitationStatus.InvalidRecipient
                            => InvalidRecipient(),
                        IssueTenantInvitationStatus.ActiveMember
                            or IssueTenantInvitationStatus.Conflict
                            => Results.Conflict(new
                            {
                                error = "An invitation cannot be issued for this recipient.",
                            }),
                        IssueTenantInvitationStatus.StaleOwner
                            => Results.Forbid(),
                        _ => throw new InvalidOperationException(
                            "Unknown invitation issuance result."),
                    };
                })
            .RequireAuthorization(TenantMembership.OwnerPolicy);

        endpoints.MapDelete(
                "/api/tenant-invitations/{invitationId:guid}",
                async (
                    Guid invitationId,
                    ClaimsPrincipal principal,
                    ITenantContext tenant,
                    ITenantInvitationStore invitations,
                    IOptions<IdentityOptions> identityOptions,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    var securityStamps = principal.FindAll(
                            identityOptions.Value.ClaimsIdentity
                                .SecurityStampClaimType)
                        .Select(claim => claim.Value)
                        .ToArray();
                    if (securityStamps.Length != 1
                        || string.IsNullOrWhiteSpace(securityStamps[0])
                        || !TenantMembership.TryGetVersion(
                            principal,
                            out var membershipVersion))
                    {
                        return Results.Forbid();
                    }

                    var result = await invitations.CancelForOwnerAsync(
                        userId,
                        securityStamps[0],
                        membershipVersion,
                        tenant.Id,
                        invitationId,
                        cancellationToken);

                    return result.Status switch
                    {
                        CancelTenantInvitationStatus.Canceled
                            or CancelTenantInvitationStatus.Unavailable
                            => Results.NoContent(),
                        CancelTenantInvitationStatus.StaleOwner
                            => Results.Forbid(),
                        CancelTenantInvitationStatus.Conflict
                            => Results.Conflict(new
                            {
                                error = "The invitation could not be cancelled.",
                            }),
                        _ => throw new InvalidOperationException(
                            "Unknown invitation cancellation result."),
                    };
                })
            .RequireAuthorization(TenantMembership.OwnerPolicy);

        endpoints.MapPost(
                "/api/tenant-invitations/accept",
                async (
                    AcceptTenantInvitationRequest request,
                    ClaimsPrincipal principal,
                    ITenantInvitationStore invitations,
                    IOptions<IdentityOptions> identityOptions,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    var securityStamps = principal.FindAll(
                            identityOptions.Value.ClaimsIdentity
                                .SecurityStampClaimType)
                        .Select(claim => claim.Value)
                        .ToArray();
                    if (securityStamps.Length != 1
                        || string.IsNullOrWhiteSpace(securityStamps[0]))
                    {
                        return CannotAccept();
                    }

                    var accepted = await invitations.AcceptAsync(
                        userId,
                        securityStamps[0],
                        request.Code ?? string.Empty,
                        cancellationToken);

                    return accepted
                        ? Results.NoContent()
                        : CannotAccept();
                })
            .RequireAuthorization();

        return endpoints;
    }

    public sealed record AcceptTenantInvitationRequest(string? Code);

    public sealed record IssueTenantInvitationRequest(string? Email);

    private static IResult QueuedInvitation(
        HttpContext httpContext,
        IssueTenantInvitationResult result)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new TenantInvitationQueuedResponse(
                result.Id!.Value,
                result.ExpiresAt!.Value,
                "queued"),
            statusCode: StatusCodes.Status202Accepted);
    }

    public sealed record TenantInvitationQueuedResponse(
        Guid Id,
        DateTimeOffset ExpiresAt,
        string DeliveryStatus);

    private static IResult InvalidRecipient() =>
        Results.BadRequest(new
        {
            error = "A valid invitation email is required.",
        });

    private static IResult CannotAccept() =>
        Results.BadRequest(new
        {
            error = "The invitation cannot be accepted.",
        });
}
