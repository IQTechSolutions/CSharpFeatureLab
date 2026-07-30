using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLegacyTenantData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__TenantBackfillGuard"
                (
                    "UnmappedRows" INTEGER NOT NULL
                        CHECK ("UnmappedRows" = 0)
                );

                INSERT INTO "__TenantBackfillGuard" ("UnmappedRows")
                SELECT count(*)
                FROM "WorkItems"
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM "AspNetUsers"
                    WHERE "AspNetUsers"."Id" = "WorkItems"."OwnerId"
                );

                INSERT INTO "__TenantBackfillGuard" ("UnmappedRows")
                SELECT count(*)
                FROM "WorkItemReports"
                LEFT JOIN "WorkItems"
                    ON "WorkItems"."Id" = "WorkItemReports"."WorkItemId"
                WHERE "WorkItems"."Id" IS NULL
                    OR "WorkItems"."OwnerId" <> "WorkItemReports"."OwnerId";

                UPDATE "AspNetUsers"
                SET "TenantId" = lower(
                    hex(randomblob(4)) || '-' ||
                    hex(randomblob(2)) || '-' ||
                    hex(randomblob(2)) || '-' ||
                    hex(randomblob(2)) || '-' ||
                    hex(randomblob(6)))
                WHERE "TenantId" = '00000000-0000-0000-0000-000000000000';

                UPDATE "WorkItems"
                SET "TenantId" =
                    (
                        SELECT "AspNetUsers"."TenantId"
                        FROM "AspNetUsers"
                        WHERE "AspNetUsers"."Id" = "WorkItems"."OwnerId"
                    )
                WHERE "TenantId" = '00000000-0000-0000-0000-000000000000';

                UPDATE "WorkItemReports"
                SET "TenantId" =
                    (
                        SELECT "WorkItems"."TenantId"
                        FROM "WorkItems"
                        WHERE "WorkItems"."Id" = "WorkItemReports"."WorkItemId"
                    )
                WHERE "TenantId" = '00000000-0000-0000-0000-000000000000';

                INSERT INTO "__TenantBackfillGuard" ("UnmappedRows")
                SELECT
                    (SELECT count(*) FROM "AspNetUsers"
                        WHERE "TenantId" = '00000000-0000-0000-0000-000000000000')
                    + (SELECT count(*) FROM "WorkItems"
                        WHERE "TenantId" = '00000000-0000-0000-0000-000000000000')
                    + (SELECT count(*) FROM "WorkItemReports"
                        WHERE "TenantId" = '00000000-0000-0000-0000-000000000000');

                DROP TABLE "__TenantBackfillGuard";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data backfills are intentionally not reversed. The surrounding
            // schema migrations remove TenantId when the full migration chain
            // is rolled back.
        }
    }
}
