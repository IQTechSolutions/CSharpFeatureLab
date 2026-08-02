using FeatureLab.Data;
using FeatureLab.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Web.Tests;

public sealed class MigrationTests
{
    [Fact]
    public async Task Migrations_apply_from_an_empty_database()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-migrations-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());

            await dbContext.Database.MigrateAsync();

            var appliedMigrations =
                await dbContext.Database.GetAppliedMigrationsAsync();
            Assert.Contains(
                appliedMigrations,
                migration => migration.EndsWith(
                    "_ScopeChatToTenant",
                    StringComparison.Ordinal));
            Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
            await AssertForeignKeysAreValidAsync(dbContext);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Migration_backfills_legacy_chat_into_the_authors_workspace()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-chat-migrations-{Guid.NewGuid():N}.db");

        try
        {
            var tenant = new TenantContext();
            var tenantId = Guid.NewGuid();
            tenant.Set(tenantId);
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, tenant);
            await dbContext.Database.MigrateAsync(
                "20260730212319_EnforceTenantReportBoundary");

            var userId = $"legacy-chat-user-{Guid.NewGuid():N}";
            var email = $"legacy-chat-{Guid.NewGuid():N}@example.test";
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AspNetUsers"
                    ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                     "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", "TenantId")
                VALUES
                    ({userId}, {email}, {email.ToUpperInvariant()}, {email},
                     {email.ToUpperInvariant()}, {false}, {false}, {false}, {true}, {0},
                     {tenantId})
                """);

            var messageId = Guid.NewGuid();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "ChatMessages"
                    ("Id", "AuthorId", "Sender", "Text", "SentAtUtc")
                VALUES
                    ({messageId}, {userId}, {"Member"}, {"Legacy tenant chat"},
                     {DateTime.UtcNow})
                """);

            await dbContext.Database.MigrateAsync();

            var migrated = await dbContext.ChatMessages
                .IgnoreQueryFilters()
                .SingleAsync(message => message.Id == messageId);
            Assert.Equal(tenantId, migrated.TenantId);
            Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Migration_fails_when_a_legacy_chat_author_cannot_be_mapped()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-orphan-chat-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());
            await dbContext.Database.MigrateAsync(
                "20260730212319_EnforceTenantReportBoundary");
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "ChatMessages"
                    ("Id", "AuthorId", "Sender", "Text", "SentAtUtc")
                VALUES
                    ({Guid.NewGuid()}, {"missing-chat-author"}, {"Member"},
                     {"Unmapped chat"}, {DateTime.UtcNow})
                """);

            var error = await Assert.ThrowsAsync<SqliteException>(
                () => dbContext.Database.MigrateAsync());

            Assert.Contains(
                "CHECK constraint failed",
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Migration_backfills_legacy_rows_into_the_owners_workspace()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-legacy-migrations-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());
            await dbContext.Database.MigrateAsync(
                "20260729211126_AddWorkItemReports");

            var userId = $"legacy-user-{Guid.NewGuid():N}";
            var email = $"legacy-{Guid.NewGuid():N}@example.test";
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AspNetUsers"
                    ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                     "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount")
                VALUES
                    ({userId}, {email}, {email.ToUpperInvariant()}, {email},
                     {email.ToUpperInvariant()}, {false}, {false}, {false}, {true}, {0})
                """);

            var workItemId = Guid.NewGuid();
            var reportId = Guid.NewGuid();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "WorkItems"
                    ("Id", "Title", "IsCompleted", "CreatedAtUtc", "OwnerId", "Version")
                VALUES
                    ({workItemId}, {"Legacy work item"}, {false}, {DateTime.UtcNow}, {userId}, {1})
                """);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "WorkItemReports"
                    ("Id", "WorkItemId", "OwnerId", "RequestedAtUtc", "CompletedAtUtc", "Content")
                VALUES
                    ({reportId}, {workItemId}, {userId}, {DateTime.UtcNow}, {null}, {null})
                """);

            await dbContext.Database.MigrateAsync();

            var migratedUser = await dbContext.Users
                .SingleAsync(user => user.Id == userId);
            var migratedWorkItem = await dbContext.WorkItems
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == workItemId);
            var migratedReport = await dbContext.WorkItemReports
                .IgnoreQueryFilters()
                .SingleAsync(report => report.Id == reportId);
            Assert.NotEqual(Guid.Empty, migratedUser.TenantId);
            Assert.Equal(migratedUser.TenantId, migratedWorkItem.TenantId);
            Assert.Equal(migratedUser.TenantId, migratedReport.TenantId);
            Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
            await AssertForeignKeysAreValidAsync(dbContext);

            await Assert.ThrowsAsync<SqliteException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO "WorkItemReports"
                        ("Id", "WorkItemId", "OwnerId", "TenantId",
                         "RequestedAtUtc", "CompletedAtUtc", "Content")
                    VALUES
                        ({Guid.NewGuid()}, {workItemId}, {userId}, {Guid.NewGuid()},
                         {DateTime.UtcNow}, {null}, {null})
                    """));
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Migration_fails_when_legacy_ownership_cannot_be_mapped()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-orphan-migrations-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());
            await dbContext.Database.MigrateAsync(
                "20260729211126_AddWorkItemReports");

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "WorkItems"
                    ("Id", "Title", "IsCompleted", "CreatedAtUtc", "OwnerId", "Version")
                VALUES
                    ({Guid.NewGuid()}, {"Unmapped legacy work"}, {false}, {DateTime.UtcNow},
                     {"missing-owner"}, {1})
                """);

            var error = await Assert.ThrowsAsync<SqliteException>(
                () => dbContext.Database.MigrateAsync());

            Assert.Contains(
                "CHECK constraint failed",
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static async Task AssertForeignKeysAreValidAsync(
        FeatureLabDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.False(await reader.ReadAsync());
    }
}
