using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Publishing.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "publishing");

            migrationBuilder.CreateTable(
                name: "channel_accounts",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TokenCipher = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pending_connections",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PagesJsonCipher = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "published_posts",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderPostId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RecordedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_published_posts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "publishing_attempts",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publishing_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_accounts_ProductId_Channel",
                schema: "publishing",
                table: "channel_accounts",
                columns: new[] { "ProductId", "Channel" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_accounts_Provider_ExternalId_ProductId",
                schema: "publishing",
                table: "channel_accounts",
                columns: new[] { "Provider", "ExternalId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_published_posts_ContentItemId_Version",
                schema: "publishing",
                table: "published_posts",
                columns: new[] { "ContentItemId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_published_posts_ProductId",
                schema: "publishing",
                table: "published_posts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_publishing_attempts_ContentItemId_Version_Number",
                schema: "publishing",
                table: "publishing_attempts",
                columns: new[] { "ContentItemId", "Version", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_accounts",
                schema: "publishing");

            migrationBuilder.DropTable(
                name: "pending_connections",
                schema: "publishing");

            migrationBuilder.DropTable(
                name: "published_posts",
                schema: "publishing");

            migrationBuilder.DropTable(
                name: "publishing_attempts",
                schema: "publishing");
        }
    }
}
