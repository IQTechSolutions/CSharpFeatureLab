using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FeatureLab.Tenancy;

public sealed class ActiveTenantMembershipRequirement
    : IAuthorizationRequirement
{
    public static ActiveTenantMembershipRequirement Instance { get; } = new();

    private ActiveTenantMembershipRequirement()
    {
    }
}

public sealed class ActiveTenantMembershipHandler(
    ITenantMembershipStore memberships,
    IOptions<IdentityOptions> identityOptions)
    : AuthorizationHandler<ActiveTenantMembershipRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveTenantMembershipRequirement requirement)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var securityStamps = context.User.FindAll(
                identityOptions.Value.ClaimsIdentity.SecurityStampClaimType)
            .Select(claim => claim.Value)
            .ToArray();
        if (string.IsNullOrWhiteSpace(userId)
            || securityStamps.Length != 1
            || string.IsNullOrWhiteSpace(securityStamps[0])
            || !TenantMembership.TryGetTenantId(
                context.User,
                out var tenantId))
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext
            ? httpContext.RequestAborted
            : CancellationToken.None;

        if (await memberships.IsActiveAsync(
            userId,
            tenantId,
            securityStamps[0],
            cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}
