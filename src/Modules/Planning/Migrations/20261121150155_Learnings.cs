using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Planning.Migrations
{
    /// <inheritdoc />
    public partial class Learnings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HypothesisId",
                schema: "planning",
                table: "plan_slots",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "learnings",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Statement = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    HowToTest = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Confidence = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourceReviewId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimesTested = table.Column<int>(type: "integer", nullable: false),
                    LastTestedPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learnings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learnings_ProductId_Status",
                schema: "planning",
                table: "learnings",
                columns: new[] { "ProductId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learnings",
                schema: "planning");

            migrationBuilder.DropColumn(
                name: "HypothesisId",
                schema: "planning",
                table: "plan_slots");
        }
    }
}
