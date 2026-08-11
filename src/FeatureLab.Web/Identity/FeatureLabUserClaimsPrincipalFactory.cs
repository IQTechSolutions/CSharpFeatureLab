using System.Security.Claims;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FeatureLab.Identity;

public sealed class FeatureLabUserClaimsPrincipalFactory(
    UserManager<FeatureLabUser> userManager,
    IOptions<IdentityOptions> optionsAccessor,
    ITenantMembershipStore memberships)
    : UserClaimsPrincipalFactory<FeatureLabUser>(
        userManager,
        optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(
        FeatureLabUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        var membershipVersion = await memberships.GetActiveVersionAsync(
            user.Id,
            user.TenantId);
        if (membershipVersion is { } version)
        {
            identity.AddClaim(
                new Claim(
                    TenantMembership.ClaimType,
                    user.TenantId.ToString()));
            identity.AddClaim(
                new Claim(
                    TenantMembership.VersionClaimType,
                    version.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
        }

        return identity;
    }
}
