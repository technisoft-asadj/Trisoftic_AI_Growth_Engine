using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Crm.Migrations
{
    /// <inheritdoc />
    public partial class Leads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_forms",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConsentText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerPool = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RoundRobinCursor = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_forms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "leads",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    FormId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    NextAction = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NextActionDue = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    QualificationNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DisqualifyReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConvertedOpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstContactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    QualifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_leads_contacts_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "crm",
                        principalTable: "contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "score_rules",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Field = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_score_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lead_forms_ProductId",
                schema: "crm",
                table: "lead_forms",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_forms_PublicKey",
                schema: "crm",
                table: "lead_forms",
                column: "PublicKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leads_NextActionDue",
                schema: "crm",
                table: "leads",
                column: "NextActionDue");

            migrationBuilder.CreateIndex(
                name: "IX_leads_OwnerUserId",
                schema: "crm",
                table: "leads",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_leads_ProductId_Status",
                schema: "crm",
                table: "leads",
                columns: new[] { "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_lead_one_open_per_contact_product",
                schema: "crm",
                table: "leads",
                columns: new[] { "ContactId", "ProductId" },
                unique: true,
                filter: "\"Status\" IN ('New','Contacted','Qualified')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_forms",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "leads",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "score_rules",
                schema: "crm");
        }
    }
}
