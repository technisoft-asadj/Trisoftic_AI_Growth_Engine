using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Governance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "governance");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: true),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pause_flags",
                schema: "governance",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pause_flags", x => x.Scope);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_OccurredAt",
                schema: "governance",
                table: "audit_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ProductId",
                schema: "governance",
                table: "audit_events",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_SubjectType_SubjectId",
                schema: "governance",
                table: "audit_events",
                columns: new[] { "SubjectType", "SubjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "pause_flags",
                schema: "governance");
        }
    }
}
