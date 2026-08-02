using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeChatToTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_SentAtUtc_Id",
                table: "ChatMessages");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ChatMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__ChatTenantBackfillGuard"
                (
                    "UnmappedRows" INTEGER NOT NULL
                        CHECK ("UnmappedRows" = 0)
                );

                INSERT INTO "__ChatTenantBackfillGuard" ("UnmappedRows")
                SELECT count(*)
                FROM "ChatMessages"
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM "AspNetUsers"
                    WHERE "AspNetUsers"."Id" = "ChatMessages"."AuthorId"
                        AND "AspNetUsers"."TenantId"
                            <> '00000000-0000-0000-0000-000000000000'
                );

                UPDATE "ChatMessages"
                SET "TenantId" =
                    (
                        SELECT "AspNetUsers"."TenantId"
                        FROM "AspNetUsers"
                        WHERE "AspNetUsers"."Id" = "ChatMessages"."AuthorId"
                    )
                WHERE "TenantId" = '00000000-0000-0000-0000-000000000000';

                INSERT INTO "__ChatTenantBackfillGuard" ("UnmappedRows")
                SELECT count(*)
                FROM "ChatMessages"
                WHERE "TenantId" = '00000000-0000-0000-0000-000000000000';

                DROP TABLE "__ChatTenantBackfillGuard";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_TenantId_SentAtUtc_Id",
                table: "ChatMessages",
                columns: new[] { "TenantId", "SentAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_TenantId_SentAtUtc_Id",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ChatMessages");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SentAtUtc_Id",
                table: "ChatMessages",
                columns: new[] { "SentAtUtc", "Id" });
        }
    }
}
