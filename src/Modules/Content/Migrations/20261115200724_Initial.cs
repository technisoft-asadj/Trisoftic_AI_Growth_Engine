using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Content.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Objective = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Audience = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Market = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Channels = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DestinationUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TrackingNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BudgetUsd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SuccessThresholds = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "items",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Channel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Format = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    ApprovedVersion = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "versions",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceDraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_versions_items_ContentItemId",
                        column: x => x.ContentItemId,
                        principalSchema: "content",
                        principalTable: "items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_ProductId",
                schema: "content",
                table: "campaigns",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_items_CampaignId",
                schema: "content",
                table: "items",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_items_ProductId_Status",
                schema: "content",
                table: "items",
                columns: new[] { "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_items_ScheduledFor",
                schema: "content",
                table: "items",
                column: "ScheduledFor");

            migrationBuilder.CreateIndex(
                name: "IX_versions_ContentItemId_Number",
                schema: "content",
                table: "versions",
                columns: new[] { "ContentItemId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "content");

            migrationBuilder.DropTable(
                name: "versions",
                schema: "content");

            migrationBuilder.DropTable(
                name: "items",
                schema: "content");
        }
    }
}
