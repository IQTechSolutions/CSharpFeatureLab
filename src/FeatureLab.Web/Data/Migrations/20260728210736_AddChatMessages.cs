using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SentAtUtc_Id",
                table: "ChatMessages",
                columns: new[] { "SentAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages");
        }
    }
}
