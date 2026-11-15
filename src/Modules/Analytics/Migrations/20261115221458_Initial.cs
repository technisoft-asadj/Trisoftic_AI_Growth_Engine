using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Analytics.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "data_sources",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FreshnessHours = table.Column<int>(type: "integer", nullable: false),
                    LastRetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastStaleAlertAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_batches",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    Replaced = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RolledBackAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RolledBackBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "metric_definitions",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Aggregation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Definition = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "metric_observations",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetricKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Channel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Market = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_observations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_sources_ProductId_Name",
                schema: "analytics",
                table: "data_sources",
                columns: new[] { "ProductId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_batches_ProductId_CreatedAt",
                schema: "analytics",
                table: "import_batches",
                columns: new[] { "ProductId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_metric_definitions_Key",
                schema: "analytics",
                table: "metric_definitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metric_observations_ImportBatchId",
                schema: "analytics",
                table: "metric_observations",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_metric_observations_ProductId_DataSourceId_MetricKey_Period~",
                schema: "analytics",
                table: "metric_observations",
                columns: new[] { "ProductId", "DataSourceId", "MetricKey", "PeriodStart", "PeriodEnd", "Channel", "Market", "CampaignId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metric_observations_ProductId_MetricKey_PeriodStart",
                schema: "analytics",
                table: "metric_observations",
                columns: new[] { "ProductId", "MetricKey", "PeriodStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_sources",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "import_batches",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "metric_definitions",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "metric_observations",
                schema: "analytics");
        }
    }
}
