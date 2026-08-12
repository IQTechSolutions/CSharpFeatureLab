using FeatureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Tenancy;

public interface ITenantMembershipStore
{
    Task<IReadOnlyList<TenantMembershipOption>?> ListActiveAsync(
        string userId,
        string securityStamp,
        CancellationToken cancellationToken = default);

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

    Task<SelectTenantMembershipResult> SelectAsync(
        string userId,
        string securityStamp,
        Guid tenantId,
        CancellationToken cancellationToken = default);
}

public sealed record TenantMembershipOption(
    Guid TenantId,
    bool IsSelected);

public enum SelectTenantMembershipResult
{
    Selected,
    AlreadySelected,
    NotFound,
    StaleIdentity,
    Conflict,
}

public sealed class EfTenantMembershipStore(
    FeatureLabDbContext dbContext,
    TimeProvider timeProvider) : ITenantMembershipStore
{
    public async Task<IReadOnlyList<TenantMembershipOption>?> ListActiveAsync(
        string userId,
        string securityStamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(securityStamp))
        {
            return null;
        }

        var rows = await (
                from user in dbContext.Users.AsNoTracking()
                where user.Id == userId
                    && user.SecurityStamp == securityStamp
                join membership in dbContext.TenantMemberships
                        .AsNoTracking()
                        .Where(membership => membership.IsActive)
                    on user.Id equals membership.UserId
                    into activeMemberships
                from membership in activeMemberships.DefaultIfEmpty()
                select new
                {
                    SelectedTenantId = user.TenantId,
                    MembershipTenantId = membership == null
                        ? (Guid?)null
                        : membership.TenantId,
                })
            .ToArrayAsync(cancellationToken);
        if (rows.Length == 0)
        {
            return null;
        }

        return rows
            .Where(row => row.MembershipTenantId is not null)
            .OrderBy(row => row.MembershipTenantId)
            .Select(row => new TenantMembershipOption(
                row.MembershipTenantId!.Value,
                row.MembershipTenantId == row.SelectedTenantId))
            .ToArray();
    }

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

    public async Task<SelectTenantMembershipResult> SelectAsync(
        string userId,
        string securityStamp,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(securityStamp))
        {
            return SelectTenantMembershipResult.StaleIdentity;
        }

        if (tenantId == Guid.Empty)
        {
            return SelectTenantMembershipResult.NotFound;
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == userId
                && user.SecurityStamp == securityStamp,
            cancellationToken);
        if (user is null)
        {
            return SelectTenantMembershipResult.StaleIdentity;
        }

        var hasActiveMembership = await dbContext.TenantMemberships
            .AsNoTracking()
            .AnyAsync(
                membership => membership.UserId == userId
                    && membership.TenantId == tenantId
                    && membership.IsActive,
                cancellationToken);
        if (!hasActiveMembership)
        {
            return SelectTenantMembershipResult.NotFound;
        }

        if (user.TenantId == tenantId)
        {
            return SelectTenantMembershipResult.AlreadySelected;
        }

        user.TenantId = tenantId;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return SelectTenantMembershipResult.Selected;
        }
        catch (DbUpdateConcurrencyException)
        {
            return SelectTenantMembershipResult.Conflict;
        }
    }
}
