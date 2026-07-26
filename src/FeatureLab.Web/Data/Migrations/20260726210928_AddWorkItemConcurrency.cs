using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeatureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "WorkItems");
        }
    }
}
