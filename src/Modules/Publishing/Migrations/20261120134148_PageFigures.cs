using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Publishing.Migrations
{
    /// <inheritdoc />
    public partial class PageFigures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "page_figures",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Figure = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: true),
                    MetricUsed = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MeasuredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_figures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_page_figures_AccountId_Figure_Day",
                schema: "publishing",
                table: "page_figures",
                columns: new[] { "AccountId", "Figure", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_figures_ProductId",
                schema: "publishing",
                table: "page_figures",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_figures",
                schema: "publishing");
        }
    }
}
