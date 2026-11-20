using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Content.Migrations
{
    /// <inheritdoc />
    public partial class HookType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HookType",
                schema: "content",
                table: "items",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HookType",
                schema: "content",
                table: "items");
        }
    }
}
