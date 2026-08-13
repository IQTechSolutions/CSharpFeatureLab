using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantOwnerInvitationIssuance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantInvitations_TenantId_NormalizedEmail",
                table: "TenantInvitations");

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "TenantMemberships",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "TenantInvitations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedByUserId",
                table: "TenantInvitations",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "TenantInvitations"
                SET "ClosedAt" = COALESCE("AcceptedAt", CURRENT_TIMESTAMP)
                WHERE "ClosedAt" IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantMemberships_Role_Valid",
                table: "TenantMemberships",
                sql: "\"Role\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_IssuedByUserId",
                table: "TenantInvitations",
                column: "IssuedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TenantId_NormalizedEmail",
                table: "TenantInvitations",
                columns: new[] { "TenantId", "NormalizedEmail" },
                unique: true,
                filter: "\"ClosedAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_TenantInvitations_AspNetUsers_IssuedByUserId",
                table: "TenantInvitations",
                column: "IssuedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TenantInvitations_AspNetUsers_IssuedByUserId",
                table: "TenantInvitations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantMemberships_Role_Valid",
                table: "TenantMemberships");

            migrationBuilder.DropIndex(
                name: "IX_TenantInvitations_IssuedByUserId",
                table: "TenantInvitations");

            migrationBuilder.DropIndex(
                name: "IX_TenantInvitations_TenantId_NormalizedEmail",
                table: "TenantInvitations");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "TenantMemberships");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "TenantInvitations");

            migrationBuilder.DropColumn(
                name: "IssuedByUserId",
                table: "TenantInvitations");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TenantId_NormalizedEmail",
                table: "TenantInvitations",
                columns: new[] { "TenantId", "NormalizedEmail" });
        }
    }
}
