using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Planning.Migrations
{
    /// <inheritdoc />
    public partial class AutopilotMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageBrief",
                schema: "planning",
                table: "plan_slots",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Visual",
                schema: "planning",
                table: "plan_slots",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseImages",
                schema: "planning",
                table: "autopilot_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseVideos",
                schema: "planning",
                table: "autopilot_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageBrief",
                schema: "planning",
                table: "plan_slots");

            migrationBuilder.DropColumn(
                name: "Visual",
                schema: "planning",
                table: "plan_slots");

            migrationBuilder.DropColumn(
                name: "UseImages",
                schema: "planning",
                table: "autopilot_settings");

            migrationBuilder.DropColumn(
                name: "UseVideos",
                schema: "planning",
                table: "autopilot_settings");
        }
    }
}
