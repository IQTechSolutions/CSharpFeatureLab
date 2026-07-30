using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceTenantReportBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkItemReports_WorkItems_WorkItemId",
                table: "WorkItemReports");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemReports_WorkItemId",
                table: "WorkItemReports");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReports_TenantId_WorkItemId",
                table: "WorkItemReports",
                columns: new[] { "TenantId", "WorkItemId" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkItemReports_WorkItems_TenantId_WorkItemId",
                table: "WorkItemReports",
                columns: new[] { "TenantId", "WorkItemId" },
                principalTable: "WorkItems",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkItemReports_WorkItems_TenantId_WorkItemId",
                table: "WorkItemReports");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemReports_TenantId_WorkItemId",
                table: "WorkItemReports");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReports_WorkItemId",
                table: "WorkItemReports",
                column: "WorkItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkItemReports_WorkItems_WorkItemId",
                table: "WorkItemReports",
                column: "WorkItemId",
                principalTable: "WorkItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
