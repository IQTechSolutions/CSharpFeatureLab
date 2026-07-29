using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemReports_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReports_OwnerId_RequestedAtUtc",
                table: "WorkItemReports",
                columns: new[] { "OwnerId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReports_WorkItemId",
                table: "WorkItemReports",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemReports");
        }
    }
}
