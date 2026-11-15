using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Scheduling.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "appointment_types",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    BufferBeforeMinutes = table.Column<int>(type: "integer", nullable: false),
                    BufferAfterMinutes = table.Column<int>(type: "integer", nullable: false),
                    SlotStepMinutes = table.Column<int>(type: "integer", nullable: false),
                    MinNoticeHours = table.Column<int>(type: "integer", nullable: false),
                    MaxDaysAhead = table.Column<int>(type: "integer", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StaffUserIds = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointment_types", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "availability_exceptions",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_exceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_availability_exceptions_appointment_types_AppointmentTypeId",
                        column: x => x.AppointmentTypeId,
                        principalSchema: "scheduling",
                        principalTable: "appointment_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "availability_rules",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    EndMinute = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_availability_rules_appointment_types_AppointmentTypeId",
                        column: x => x.AppointmentTypeId,
                        principalSchema: "scheduling",
                        principalTable: "appointment_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BlockStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BlockEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttendeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AttendeeEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    AttendeePhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LeadId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ManageTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RescheduledFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bookings_appointment_types_AppointmentTypeId",
                        column: x => x.AppointmentTypeId,
                        principalSchema: "scheduling",
                        principalTable: "appointment_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointment_types_ProductId",
                schema: "scheduling",
                table: "appointment_types",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_appointment_types_PublicKey",
                schema: "scheduling",
                table: "appointment_types",
                column: "PublicKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_availability_exceptions_AppointmentTypeId_Date",
                schema: "scheduling",
                table: "availability_exceptions",
                columns: new[] { "AppointmentTypeId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_availability_rules_AppointmentTypeId",
                schema: "scheduling",
                table: "availability_rules",
                column: "AppointmentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_AppointmentTypeId_IdempotencyKey",
                schema: "scheduling",
                table: "bookings",
                columns: new[] { "AppointmentTypeId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_LeadId",
                schema: "scheduling",
                table: "bookings",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_ManageTokenHash",
                schema: "scheduling",
                table: "bookings",
                column: "ManageTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_ProductId_StartUtc",
                schema: "scheduling",
                table: "bookings",
                columns: new[] { "ProductId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_StaffUserId_StartUtc",
                schema: "scheduling",
                table: "bookings",
                columns: new[] { "StaffUserId", "StartUtc" });

            // Double-booking protection lives in the database, not only in application code: two confirmed bookings of the same staff
            // member whose blocks (appointment + buffers) overlap are impossible, however many requests race. The uuid is cast to text
            // because btree_gist on PostgreSQL 17 has no uuid operator class.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("""
                ALTER TABLE scheduling.bookings ADD CONSTRAINT ex_bookings_no_overlap
                EXCLUDE USING gist (("StaffUserId"::text) WITH =, tstzrange("BlockStartUtc", "BlockEndUtc") WITH &&)
                WHERE ("Status" = 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "availability_exceptions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "availability_rules",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "bookings",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "appointment_types",
                schema: "scheduling");
        }
    }
}
