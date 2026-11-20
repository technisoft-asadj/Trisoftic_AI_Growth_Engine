using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Planning.Migrations
{
    /// <inheritdoc />
    public partial class SlotHookType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HookType",
                schema: "planning",
                table: "plan_slots",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HookType",
                schema: "planning",
                table: "plan_slots");
        }
    }
}
