using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Links.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "links");

            migrationBuilder.CreateTable(
                name: "clicks",
                schema: "links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsBot = table.Column<bool>(type: "boolean", nullable: false),
                    VisitorHash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clicks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "links",
                schema: "links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Destination = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Channel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Campaign = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContentItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_clicks_LinkId_At",
                schema: "links",
                table: "clicks",
                columns: new[] { "LinkId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_links_Code",
                schema: "links",
                table: "links",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_links_ContentItemId",
                schema: "links",
                table: "links",
                column: "ContentItemId");

            migrationBuilder.CreateIndex(
                name: "IX_links_ProductId_CreatedAt",
                schema: "links",
                table: "links",
                columns: new[] { "ProductId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clicks",
                schema: "links");

            migrationBuilder.DropTable(
                name: "links",
                schema: "links");
        }
    }
}
