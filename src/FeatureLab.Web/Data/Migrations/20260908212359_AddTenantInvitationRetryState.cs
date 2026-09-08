using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantInvitationRetryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantInvitationOutbox_CreatedAt_InvitationId",
                table: "TenantInvitationOutbox");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "TenantInvitationOutbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "TenantInvitationOutbox",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "TenantInvitationOutbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "TenantInvitationOutbox"
                SET "NextAttemptAt" = "CreatedAt";
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "NextAttemptAt",
                table: "TenantInvitationOutbox",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitationOutbox_NextAttemptAt_CreatedAt_InvitationId",
                table: "TenantInvitationOutbox",
                columns: new[] { "NextAttemptAt", "CreatedAt", "InvitationId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantInvitationOutbox_AttemptCount_NonNegative",
                table: "TenantInvitationOutbox",
                sql: "\"AttemptCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantInvitationOutbox_FailureCode_Valid",
                table: "TenantInvitationOutbox",
                sql: "\"FailureCode\" IS NULL OR \"FailureCode\" IN ('provider-failure', 'delivery-timeout', 'acknowledgement-failure')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantInvitationOutbox_NextAttemptAt_CreatedAt_InvitationId",
                table: "TenantInvitationOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantInvitationOutbox_AttemptCount_NonNegative",
                table: "TenantInvitationOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantInvitationOutbox_FailureCode_Valid",
                table: "TenantInvitationOutbox");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "TenantInvitationOutbox");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "TenantInvitationOutbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "TenantInvitationOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitationOutbox_CreatedAt_InvitationId",
                table: "TenantInvitationOutbox",
                columns: new[] { "CreatedAt", "InvitationId" });
        }
    }
}
