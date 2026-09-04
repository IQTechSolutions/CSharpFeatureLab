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
                    "_AddVersionedMembershipInvitations",
                    StringComparison.Ordinal));
            Assert.Contains(
                appliedMigrations,
                migration => migration.EndsWith(
                    "_AddTenantOwnerInvitationIssuance",
                    StringComparison.Ordinal));
            Assert.Contains(
                appliedMigrations,
                migration => migration.EndsWith(
                    "_AddProtectedTenantInvitationOutbox",
                    StringComparison.Ordinal));
            Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
            Assert.False(dbContext.Database.HasPendingModelChanges());
            Assert.Empty(dbContext.TenantInvitationOutboxMessages);
            var outboxType = dbContext.Model.FindEntityType(
                typeof(TenantInvitationOutboxMessage));
            Assert.NotNull(outboxType);
            Assert.Equal(
                TenantInvitationOutboxMessage.MaximumProtectedPayloadLength,
                outboxType.FindProperty(
                    nameof(TenantInvitationOutboxMessage.ProtectedPayload))!
                    .GetMaxLength());
            Assert.Contains(
                outboxType.GetIndexes(),
                index => index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(TenantInvitationOutboxMessage.CreatedAt),
                        nameof(TenantInvitationOutboxMessage.InvitationId),
                    ]));
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
    public async Task Owner_migration_fail_closes_legacy_pending_codes_before_unique_index()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-invitation-upgrade-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());
            await dbContext.Database.MigrateAsync(
                "20260811061731_AddVersionedMembershipInvitations");

            var userId = $"legacy-invite-user-{Guid.NewGuid():N}";
            var email = $"legacy-invite-{Guid.NewGuid():N}@example.test";
            var normalizedEmail = email.ToUpperInvariant();
            var tenantId = Guid.NewGuid();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AspNetUsers"
                    ("Id", "UserName", "NormalizedUserName", "Email",
                     "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed",
                     "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount",
                     "TenantId")
                VALUES
                    ({userId}, {email}, {normalizedEmail}, {email},
                     {normalizedEmail}, {false}, {false}, {false}, {true}, {0},
                     {tenantId})
                """);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "TenantMemberships"
                    ("UserId", "TenantId", "Version", "IsActive", "CreatedAt",
                     "RemovedAt")
                VALUES
                    ({userId}, {tenantId}, {1}, {true},
                     {DateTimeOffset.UtcNow}, {null})
                """);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "TenantInvitations"
                    ("Id", "TenantId", "NormalizedEmail", "CodeHash", "ExpiresAt",
                     "AcceptedAt", "AcceptedByUserId", "Version")
                VALUES
                    ({Guid.NewGuid()}, {tenantId}, {normalizedEmail},
                     {new string('A', 64)}, {DateTimeOffset.UtcNow.AddDays(7)},
                     {null}, {null}, {1}),
                    ({Guid.NewGuid()}, {tenantId}, {normalizedEmail},
                     {new string('B', 64)}, {DateTimeOffset.UtcNow.AddDays(7)},
                     {null}, {null}, {1})
                """);

            var beforeUpgrade = DateTimeOffset.UtcNow.AddSeconds(-1);
            await dbContext.Database.MigrateAsync();
            var afterUpgrade = DateTimeOffset.UtcNow.AddSeconds(1);

            var legacyInvitations = await dbContext.TenantInvitations
                .Where(invitation => invitation.TenantId == tenantId)
                .ToListAsync();
            Assert.Equal(2, legacyInvitations.Count);
            Assert.All(
                legacyInvitations,
                invitation => Assert.InRange(
                    invitation.ClosedAt!.Value,
                    beforeUpgrade,
                    afterUpgrade));
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
    public async Task Migration_backfills_only_current_tenant_memberships()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-membership-migrations-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());
            await dbContext.Database.MigrateAsync(
                "20260802100143_ScopeChatToTenant");

            var activeUserId = $"active-member-{Guid.NewGuid():N}";
            var removedUserId = $"removed-member-{Guid.NewGuid():N}";
            var activeEmail = $"active-{Guid.NewGuid():N}@example.test";
            var removedEmail = $"removed-{Guid.NewGuid():N}@example.test";
            var activeNormalizedEmail = activeEmail.ToUpperInvariant();
            var removedNormalizedEmail = removedEmail.ToUpperInvariant();
            var tenantId = Guid.NewGuid();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AspNetUsers"
                    ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                     "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", "TenantId")
                VALUES
                    ({activeUserId}, {activeEmail}, {activeNormalizedEmail},
                     {activeEmail}, {activeNormalizedEmail}, {false}, {false},
                     {false}, {true}, {0}, {tenantId}),
                    ({removedUserId}, {removedEmail}, {removedNormalizedEmail},
                     {removedEmail}, {removedNormalizedEmail}, {false}, {false},
                     {false}, {true}, {0}, {Guid.Empty})
                """);

            await dbContext.Database.MigrateAsync();

            var migratedUsers = await dbContext.Users
                .Where(user => user.Id == activeUserId
                    || user.Id == removedUserId)
                .ToDictionaryAsync(user => user.Id);
            Assert.Equal(tenantId, migratedUsers[activeUserId].TenantId);
            Assert.Equal(Guid.Empty, migratedUsers[removedUserId].TenantId);

            var membership = await dbContext.TenantMemberships.SingleAsync();
            Assert.Equal(activeUserId, membership.UserId);
            Assert.Equal(tenantId, membership.TenantId);
            Assert.Equal(TenantMembershipRole.Member, membership.Role);
            Assert.Equal(1, membership.Version);
            Assert.True(membership.IsActive);
            Assert.NotEqual(default, membership.CreatedAt);
            Assert.Null(membership.RemovedAt);
            Assert.DoesNotContain(
                dbContext.TenantMemberships,
                candidate => candidate.UserId == removedUserId);
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
    public async Task Migration_rejects_duplicate_normalized_emails()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-duplicate-email-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());
            await dbContext.Database.MigrateAsync(
                "20260802100143_ScopeChatToTenant");

            var firstUserId = $"first-email-user-{Guid.NewGuid():N}";
            var secondUserId = $"second-email-user-{Guid.NewGuid():N}";
            var firstUserName = $"first-email-{Guid.NewGuid():N}";
            var secondUserName = $"second-email-{Guid.NewGuid():N}";
            var sharedEmail = $"duplicate-{Guid.NewGuid():N}@example.test";
            var normalizedEmail = sharedEmail.ToUpperInvariant();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AspNetUsers"
                    ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                     "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", "TenantId")
                VALUES
                    ({firstUserId}, {firstUserName}, {firstUserName.ToUpperInvariant()},
                     {sharedEmail}, {normalizedEmail}, {false}, {false}, {false}, {true},
                     {0}, {Guid.Empty}),
                    ({secondUserId}, {secondUserName}, {secondUserName.ToUpperInvariant()},
                     {sharedEmail}, {normalizedEmail}, {false}, {false}, {false}, {true},
                     {0}, {Guid.Empty})
                """);

            var error = await Assert.ThrowsAsync<SqliteException>(
                () => dbContext.Database.MigrateAsync());

            Assert.Equal(19, error.SqliteErrorCode);
            Assert.Contains(
                "AspNetUsers.NormalizedEmail",
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

    [Fact]
    public async Task Membership_role_constraint_rejects_unknown_values()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-role-constraint-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext =
                new FeatureLabDbContext(options, new TenantContext());
            await dbContext.Database.MigrateAsync();
            var userId = $"role-user-{Guid.NewGuid():N}";
            var email = $"role-{Guid.NewGuid():N}@example.test";
            var tenantId = Guid.NewGuid();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AspNetUsers"
                    ("Id", "UserName", "NormalizedUserName", "Email",
                     "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed",
                     "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount",
                     "TenantId")
                VALUES
                    ({userId}, {email}, {email.ToUpperInvariant()}, {email},
                     {email.ToUpperInvariant()}, {false}, {false}, {false}, {true},
                     {0}, {tenantId})
                """);

            var error = await Assert.ThrowsAsync<SqliteException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO "TenantMemberships"
                        ("UserId", "TenantId", "Role", "Version", "IsActive",
                         "CreatedAt", "RemovedAt")
                    VALUES
                        ({userId}, {tenantId}, {0}, {1}, {true},
                         {DateTimeOffset.UtcNow}, {null})
                    """));

            Assert.Equal(19, error.SqliteErrorCode);
            Assert.Contains(
                "CK_TenantMemberships_Role_Valid",
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
