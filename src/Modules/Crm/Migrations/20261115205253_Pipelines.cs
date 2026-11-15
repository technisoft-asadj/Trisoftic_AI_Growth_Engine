using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Crm.Migrations
{
    /// <inheritdoc />
    public partial class Pipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pipelines",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipelines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_stages",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Probability = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipeline_stages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pipeline_stages_pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalSchema: "crm",
                        principalTable: "pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "opportunities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ProbabilityOverride = table.Column<int>(type: "integer", nullable: true),
                    ExpectedCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NextStep = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    WonLostReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RevenueAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_opportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_opportunities_contacts_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "crm",
                        principalTable: "contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_opportunities_pipeline_stages_StageId",
                        column: x => x.StageId,
                        principalSchema: "crm",
                        principalTable: "pipeline_stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "opportunity_stage_changes",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_opportunity_stage_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_opportunity_stage_changes_opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "crm",
                        principalTable: "opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sales_activities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_activities_opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "crm",
                        principalTable: "opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_ContactId",
                schema: "crm",
                table: "opportunities",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_LeadId",
                schema: "crm",
                table: "opportunities",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_OwnerUserId",
                schema: "crm",
                table: "opportunities",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_PipelineId_StageId",
                schema: "crm",
                table: "opportunities",
                columns: new[] { "PipelineId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_ProductId_Status",
                schema: "crm",
                table: "opportunities",
                columns: new[] { "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_StageId",
                schema: "crm",
                table: "opportunities",
                column: "StageId");

            migrationBuilder.CreateIndex(
                name: "IX_opportunity_stage_changes_OpportunityId_ChangedAt",
                schema: "crm",
                table: "opportunity_stage_changes",
                columns: new[] { "OpportunityId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_stages_PipelineId_Order",
                schema: "crm",
                table: "pipeline_stages",
                columns: new[] { "PipelineId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_pipelines_ProductId",
                schema: "crm",
                table: "pipelines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_activities_DueAt",
                schema: "crm",
                table: "sales_activities",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_sales_activities_OpportunityId_CreatedAt",
                schema: "crm",
                table: "sales_activities",
                columns: new[] { "OpportunityId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "opportunity_stage_changes",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "sales_activities",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "opportunities",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "pipeline_stages",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "pipelines",
                schema: "crm");
        }
    }
}
