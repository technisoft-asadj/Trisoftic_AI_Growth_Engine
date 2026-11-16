using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Assets.Migrations
{
    /// <inheritdoc />
    public partial class AssetVideoInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DurationSeconds",
                schema: "assets",
                table: "assets",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Height",
                schema: "assets",
                table: "assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                schema: "assets",
                table: "assets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "Height",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "Width",
                schema: "assets",
                table: "assets");
        }
    }
}
