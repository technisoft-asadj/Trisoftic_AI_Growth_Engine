using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Jobs.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "jobs");

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    RunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_IdempotencyKey",
                schema: "jobs",
                table: "jobs",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_ProductId",
                schema: "jobs",
                table: "jobs",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_RunAt",
                schema: "jobs",
                table: "jobs",
                columns: new[] { "Status", "RunAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs",
                schema: "jobs");
        }
    }
}
