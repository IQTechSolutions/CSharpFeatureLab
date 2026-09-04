using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectedTenantInvitationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantInvitationOutbox",
                columns: table => new
                {
                    InvitationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantInvitationOutbox", x => x.InvitationId);
                    table.ForeignKey(
                        name: "FK_TenantInvitationOutbox_TenantInvitations_InvitationId",
                        column: x => x.InvitationId,
                        principalTable: "TenantInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitationOutbox_CreatedAt_InvitationId",
                table: "TenantInvitationOutbox",
                columns: new[] { "CreatedAt", "InvitationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantInvitationOutbox");
        }
    }
}
