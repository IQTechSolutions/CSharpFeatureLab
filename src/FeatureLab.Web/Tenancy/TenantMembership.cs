using System.Security.Claims;
using System.Globalization;

namespace FeatureLab.Tenancy;

public static class TenantMembership
{
    public const string Policy = "TenantMember";

    public const string OwnerPolicy = "TenantOwner";

    public const string ClaimType = "tenant_id";

    public const string VersionClaimType = "tenant_membership_version";

    public static bool HasValidTenantId(ClaimsPrincipal principal) =>
        TryGetTenantId(principal, out _);

    public static bool TryGetTenantId(
        ClaimsPrincipal principal,
        out Guid tenantId)
    {
        var tenantClaims = principal.FindAll(ClaimType).ToArray();
        if (tenantClaims.Length != 1)
        {
            tenantId = Guid.Empty;
            return false;
        }

        return Guid.TryParse(tenantClaims[0].Value, out tenantId)
            && tenantId != Guid.Empty;
    }

    public static bool TryGetVersion(
        ClaimsPrincipal principal,
        out long version)
    {
        var versionClaims = principal.FindAll(VersionClaimType).ToArray();
        if (versionClaims.Length != 1)
        {
            version = 0;
            return false;
        }

        return long.TryParse(
                versionClaims[0].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version)
            && version > 0;
    }
}

public interface ITenantContext
{
    Guid Id { get; }
}

public sealed class TenantContext : ITenantContext
{
    private Guid? _tenantId;

    public Guid Id =>
        _tenantId
        ?? throw new InvalidOperationException(
            "No tenant has been established for this scope.");

    public void Set(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty tenant identifier is required.",
                nameof(tenantId));
        }

        if (_tenantId is { } existingTenantId
            && existingTenantId != tenantId)
        {
            throw new InvalidOperationException(
                "The tenant cannot change inside one service scope.");
        }

        _tenantId = tenantId;
    }
}
