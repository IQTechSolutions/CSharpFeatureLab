using FeatureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Tenancy;

public interface ITenantMembershipStore
{
    Task<bool> IsActiveAsync(
        string userId,
        Guid tenantId,
        string securityStamp,
        long membershipVersion,
        CancellationToken cancellationToken = default);

    Task<long?> GetActiveVersionAsync(
        string userId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        string userId,
        Guid tenantId,
        CancellationToken cancellationToken = default);
}

public sealed class EfTenantMembershipStore(
    FeatureLabDbContext dbContext,
    TimeProvider timeProvider) : ITenantMembershipStore
{
    public Task<bool> IsActiveAsync(
        string userId,
        Guid tenantId,
        string securityStamp,
        long membershipVersion,
        CancellationToken cancellationToken = default) =>
        tenantId == Guid.Empty
            || membershipVersion <= 0
            || string.IsNullOrWhiteSpace(securityStamp)
            ? Task.FromResult(false)
            : (
                from membership in dbContext.TenantMemberships.AsNoTracking()
                join user in dbContext.Users.AsNoTracking()
                    on membership.UserId equals user.Id
                where membership.UserId == userId
                    && membership.TenantId == tenantId
                    && membership.IsActive
                    && membership.Version == membershipVersion
                    && user.TenantId == tenantId
                    && user.SecurityStamp == securityStamp
                select membership)
                .AnyAsync(cancellationToken);

    public async Task<long?> GetActiveVersionAsync(
        string userId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || tenantId == Guid.Empty)
        {
            return null;
        }

        return await dbContext.TenantMemberships
                .AsNoTracking()
                .Where(membership => membership.UserId == userId
                    && membership.TenantId == tenantId
                    && membership.IsActive)
                .Select(membership => (long?)membership.Version)
                .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(
        string userId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == userId
                && user.TenantId == tenantId,
            cancellationToken);

        var membership = await dbContext.TenantMemberships
            .SingleOrDefaultAsync(
                membership => membership.UserId == userId
                    && membership.TenantId == tenantId
                    && membership.IsActive,
                cancellationToken);

        if (user is null || membership is null)
        {
            return false;
        }

        membership.Remove(timeProvider.GetUtcNow());
        user.TenantId = Guid.Empty;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
