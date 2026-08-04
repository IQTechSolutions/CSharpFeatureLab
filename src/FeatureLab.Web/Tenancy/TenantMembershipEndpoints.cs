using System.Security.Claims;

namespace FeatureLab.Tenancy;

public static class TenantMembershipEndpoints
{
    public static IEndpointRouteBuilder MapTenantMembershipEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
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
}
