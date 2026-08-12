using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FeatureLab.Tenancy;

public static class TenantMembershipEndpoints
{
    public static IEndpointRouteBuilder MapTenantMembershipEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/tenant-memberships",
                async (
                    ClaimsPrincipal principal,
                    ITenantMembershipStore memberships,
                    IOptions<IdentityOptions> identityOptions,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    if (!TryGetSecurityStamp(
                        principal,
                        identityOptions.Value,
                        out var securityStamp))
                    {
                        return Results.Forbid();
                    }

                    var activeMemberships = await memberships.ListActiveAsync(
                        userId,
                        securityStamp,
                        cancellationToken);

                    return activeMemberships is null
                        ? Results.Forbid()
                        : Results.Ok(activeMemberships);
                })
            .RequireAuthorization();

        endpoints.MapPut(
                "/api/tenant-membership",
                async (
                    SelectTenantMembershipRequest request,
                    ClaimsPrincipal principal,
                    ITenantMembershipStore memberships,
                    IOptions<IdentityOptions> identityOptions,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    if (!TryGetSecurityStamp(
                        principal,
                        identityOptions.Value,
                        out var securityStamp))
                    {
                        return Results.Forbid();
                    }

                    if (request.TenantId == Guid.Empty)
                    {
                        return Results.BadRequest(new
                        {
                            error = "A workspace selection is required.",
                        });
                    }

                    var result = await memberships.SelectAsync(
                        userId,
                        securityStamp,
                        request.TenantId,
                        cancellationToken);

                    return result switch
                    {
                        SelectTenantMembershipResult.Selected
                            or SelectTenantMembershipResult.AlreadySelected
                            => Results.NoContent(),
                        SelectTenantMembershipResult.NotFound
                            => Results.NotFound(),
                        SelectTenantMembershipResult.StaleIdentity
                            => Results.Forbid(),
                        SelectTenantMembershipResult.Conflict
                            => Results.Conflict(new
                            {
                                error = "The workspace selection changed. Sign in and try again.",
                            }),
                        _ => throw new InvalidOperationException(
                            "Unknown membership selection result."),
                    };
                })
            .RequireAuthorization();

        endpoints.MapDelete(
                "/api/tenant-membership",
                async (
                    ClaimsPrincipal principal,
                    ITenantContext tenant,
                    ITenantMembershipStore memberships,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    var removed = await memberships.RemoveAsync(
                        userId,
                        tenant.Id,
                        cancellationToken);

                    return removed
                        ? Results.NoContent()
                        : Results.NotFound();
                })
            .RequireAuthorization(TenantMembership.Policy);

        return endpoints;
    }

    public sealed record SelectTenantMembershipRequest(Guid TenantId);

    private static bool TryGetSecurityStamp(
        ClaimsPrincipal principal,
        IdentityOptions identityOptions,
        out string securityStamp)
    {
        var securityStamps = principal.FindAll(
                identityOptions.ClaimsIdentity.SecurityStampClaimType)
            .Select(claim => claim.Value)
            .ToArray();
        if (securityStamps.Length == 1
            && !string.IsNullOrWhiteSpace(securityStamps[0]))
        {
            securityStamp = securityStamps[0];
            return true;
        }

        securityStamp = string.Empty;
        return false;
    }
}
