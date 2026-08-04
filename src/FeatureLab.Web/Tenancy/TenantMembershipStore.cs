using FeatureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Tenancy;

public interface ITenantMembershipStore
{
    Task<bool> IsActiveAsync(
        string userId,
        Guid tenantId,
        string securityStamp,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        string userId,
        Guid tenantId,
        CancellationToken cancellationToken = default);
}

public sealed class EfTenantMembershipStore(
    FeatureLabDbContext dbContext) : ITenantMembershipStore
{
    public Task<bool> IsActiveAsync(
        string userId,
        Guid tenantId,
        string securityStamp,
        CancellationToken cancellationToken = default) =>
        tenantId == Guid.Empty
            || string.IsNullOrWhiteSpace(securityStamp)
            ? Task.FromResult(false)
            : dbContext.Users
                .AsNoTracking()
                .AnyAsync(
                    user => user.Id == userId
                        && user.TenantId == tenantId
                        && user.SecurityStamp == securityStamp,
                    cancellationToken);

    public async Task<bool> RemoveAsync(
        string userId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == userId
                && user.TenantId == tenantId,
            cancellationToken);

        if (user is null)
        {
            return false;
        }

        user.TenantId = Guid.Empty;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
