using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Publishing.Migrations
{
    /// <inheritdoc />
    public partial class PostComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "post_comments",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublishedPostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderCommentId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CommentedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Draft = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    SuggestHide = table.Column<bool>(type: "boolean", nullable: false),
                    MissingFacts = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DraftedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplyText = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    RepliedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RepliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProviderReplyId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    HiddenBy = table.Column<Guid>(type: "uuid", nullable: true),
                    HiddenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_comments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_post_comments_AccountId_ProviderCommentId",
                schema: "publishing",
                table: "post_comments",
                columns: new[] { "AccountId", "ProviderCommentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_post_comments_ProductId_Status_CommentedAt",
                schema: "publishing",
                table: "post_comments",
                columns: new[] { "ProductId", "Status", "CommentedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "post_comments",
                schema: "publishing");
        }
    }
}
