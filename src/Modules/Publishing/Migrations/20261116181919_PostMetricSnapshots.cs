using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Publishing.Migrations
{
    /// <inheritdoc />
    public partial class PostMetricSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "post_metric_snapshots",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublishedPostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    TakenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reactions = table.Column<int>(type: "integer", nullable: true),
                    Comments = table.Column<int>(type: "integer", nullable: true),
                    Shares = table.Column<int>(type: "integer", nullable: true),
                    Views = table.Column<long>(type: "bigint", nullable: true),
                    Reach = table.Column<long>(type: "bigint", nullable: true),
                    Clicks = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_metric_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_post_metric_snapshots_ProductId",
                schema: "publishing",
                table: "post_metric_snapshots",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_post_metric_snapshots_PublishedPostId_TakenAt",
                schema: "publishing",
                table: "post_metric_snapshots",
                columns: new[] { "PublishedPostId", "TakenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "post_metric_snapshots",
                schema: "publishing");
        }
    }
}
