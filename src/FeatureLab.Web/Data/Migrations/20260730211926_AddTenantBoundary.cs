using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_OwnerId_CreatedAtUtc",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemReports_OwnerId_RequestedAtUtc",
                table: "WorkItemReports");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "WorkItems",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "WorkItemReports",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddUniqueConstraint(
                name: "AK_WorkItems_TenantId_Id",
                table: "WorkItems",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_TenantId_OwnerId_CreatedAtUtc",
                table: "WorkItems",
                columns: new[] { "TenantId", "OwnerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReports_TenantId_OwnerId_RequestedAtUtc",
                table: "WorkItemReports",
                columns: new[] { "TenantId", "OwnerId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_WorkItems_TenantId_Id",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItems_TenantId_OwnerId_CreatedAtUtc",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemReports_TenantId_OwnerId_RequestedAtUtc",
                table: "WorkItemReports");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "WorkItemReports");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_OwnerId_CreatedAtUtc",
                table: "WorkItems",
                columns: new[] { "OwnerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReports_OwnerId_RequestedAtUtc",
                table: "WorkItemReports",
                columns: new[] { "OwnerId", "RequestedAtUtc" });
        }
    }
}
