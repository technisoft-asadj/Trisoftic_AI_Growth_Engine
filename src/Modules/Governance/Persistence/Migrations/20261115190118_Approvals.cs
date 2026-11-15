using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Governance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Approvals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubjectVersion = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approvals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approvals_Status_ProductId",
                schema: "governance",
                table: "approvals",
                columns: new[] { "Status", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_approvals_SubjectType_SubjectId",
                schema: "governance",
                table: "approvals",
                columns: new[] { "SubjectType", "SubjectId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_approvals_SubjectType_SubjectId_SubjectVersion",
                schema: "governance",
                table: "approvals",
                columns: new[] { "SubjectType", "SubjectId", "SubjectVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals",
                schema: "governance");
        }
    }
}
