using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Planning.Migrations
{
    /// <inheritdoc />
    public partial class HookTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TestId",
                schema: "planning",
                table: "plan_slots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Variant",
                schema: "planning",
                table: "plan_slots",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "hook_tests",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Channel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Brief = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    VariantsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PostsPerVariant = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcludedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hook_tests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plan_slots_TestId",
                schema: "planning",
                table: "plan_slots",
                column: "TestId");

            migrationBuilder.CreateIndex(
                name: "IX_hook_tests_ProductId_CreatedAt",
                schema: "planning",
                table: "hook_tests",
                columns: new[] { "ProductId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hook_tests",
                schema: "planning");

            migrationBuilder.DropIndex(
                name: "IX_plan_slots_TestId",
                schema: "planning",
                table: "plan_slots");

            migrationBuilder.DropColumn(
                name: "TestId",
                schema: "planning",
                table: "plan_slots");

            migrationBuilder.DropColumn(
                name: "Variant",
                schema: "planning",
                table: "plan_slots");
        }
    }
}
