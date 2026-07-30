using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using FeatureLab.Tenancy;

namespace FeatureLab.Data;

public sealed class FeatureLabDbContextFactory : IDesignTimeDbContextFactory<FeatureLabDbContext>
{
    public FeatureLabDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
            .UseSqlite("Data Source=app.db")
            .Options;

        return new FeatureLabDbContext(options, new TenantContext());
    }
}
