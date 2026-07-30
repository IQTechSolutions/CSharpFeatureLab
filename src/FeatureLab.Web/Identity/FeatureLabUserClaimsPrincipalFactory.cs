using System.Security.Claims;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FeatureLab.Identity;

public sealed class FeatureLabUserClaimsPrincipalFactory(
    UserManager<FeatureLabUser> userManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<FeatureLabUser>(
        userManager,
        optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(
        FeatureLabUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (user.TenantId != Guid.Empty)
        {
            identity.AddClaim(
                new Claim(
                    TenantMembership.ClaimType,
                    user.TenantId.ToString()));
        }

        return identity;
    }
}
