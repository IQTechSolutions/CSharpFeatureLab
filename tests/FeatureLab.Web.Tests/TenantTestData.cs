using FeatureLab.Data;
using FeatureLab.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

internal static class TenantTestData
{
    public static async Task<(string UserId, Guid TenantId)> ProvisionAsync(
        IServiceProvider services,
        string email,
        Guid tenantId,
        bool select = true)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Tests must provision a non-empty tenant identifier.",
                nameof(tenantId));
        }

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var user = await dbContext.Users.SingleAsync(
            user => user.Email == email);
        var membership = await dbContext.TenantMemberships
            .SingleOrDefaultAsync(
                membership => membership.UserId == user.Id
                    && membership.TenantId == tenantId);

        if (membership is null)
        {
            dbContext.TenantMemberships.Add(
                TenantMembershipRecord.Create(
                    user.Id,
                    tenantId,
                    DateTimeOffset.UtcNow));
        }
        else if (!membership.IsActive)
        {
            membership.Reactivate();
        }

        if (select)
        {
            user.TenantId = tenantId;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        }
        await dbContext.SaveChangesAsync();

        return (user.Id, tenantId);
    }
}
