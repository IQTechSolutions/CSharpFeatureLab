using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionedMembershipInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateTable(
                name: "TenantInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantInvitations", x => x.Id);
                    table.CheckConstraint("CK_TenantInvitations_Version_Positive", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_TenantInvitations_AspNetUsers_AcceptedByUserId",
                        column: x => x.AcceptedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantMemberships",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMemberships", x => new { x.UserId, x.TenantId });
                    table.CheckConstraint("CK_TenantMemberships_Version_Positive", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_TenantMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_AcceptedByUserId",
                table: "TenantInvitations",
                column: "AcceptedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_CodeHash",
                table: "TenantInvitations",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TenantId_NormalizedEmail",
                table: "TenantInvitations",
                columns: new[] { "TenantId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMemberships_TenantId_IsActive",
                table: "TenantMemberships",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.Sql(
                """
                INSERT INTO "TenantMemberships"
                    ("UserId", "TenantId", "Version", "IsActive", "CreatedAt", "RemovedAt")
                SELECT
                    "Id", "TenantId", 1, 1, CURRENT_TIMESTAMP, NULL
                FROM "AspNetUsers"
                WHERE "TenantId" <> '00000000-0000-0000-0000-000000000000';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantInvitations");

            migrationBuilder.DropTable(
                name: "TenantMemberships");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");
        }
    }
}
