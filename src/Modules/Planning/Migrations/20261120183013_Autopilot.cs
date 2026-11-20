using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Planning.Migrations
{
    /// <inheritdoc />
    public partial class Autopilot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Managed",
                schema: "planning",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                schema: "planning",
                table: "plan_slots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Guidance",
                schema: "planning",
                table: "plan_slots",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "autopilot_decisions",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    SlotId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContentItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Reasons = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ChecksJson = table.Column<string>(type: "jsonb", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopilot_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "autopilot_settings",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Channel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Goal = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PlanDays = table.Column<int>(type: "integer", nullable: false),
                    EveryDays = table.Column<int>(type: "integer", nullable: false),
                    PostTimeUtc = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    MaxPostsPerDay = table.Column<int>(type: "integer", nullable: false),
                    LookaheadHours = table.Column<int>(type: "integer", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopilot_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_autopilot_decisions_ContentItemId",
                schema: "planning",
                table: "autopilot_decisions",
                column: "ContentItemId");

            migrationBuilder.CreateIndex(
                name: "IX_autopilot_decisions_ProductId_CreatedAt",
                schema: "planning",
                table: "autopilot_decisions",
                columns: new[] { "ProductId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_autopilot_settings_ProductId",
                schema: "planning",
                table: "autopilot_settings",
                column: "ProductId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "autopilot_decisions",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "autopilot_settings",
                schema: "planning");

            migrationBuilder.DropColumn(
                name: "Managed",
                schema: "planning",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Attempts",
                schema: "planning",
                table: "plan_slots");

            migrationBuilder.DropColumn(
                name: "Guidance",
                schema: "planning",
                table: "plan_slots");
        }
    }
}
