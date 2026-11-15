using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "automation");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepIndex = table.Column<int>(type: "integer", nullable: true),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssigneeUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepIndex = table.Column<int>(type: "integer", nullable: true),
                    OverdueNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_receipts",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SignatureHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_receipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventDataJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    NextStep = table.Column<int>(type: "integer", nullable: false),
                    ResumeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_runs_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalSchema: "automation",
                        principalTable: "workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_versions_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalSchema: "automation",
                        principalTable: "workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "action_logs",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_action_logs_workflow_runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "automation",
                        principalTable: "workflow_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_action_logs_RunId_At",
                schema: "automation",
                table: "action_logs",
                columns: new[] { "RunId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_RunId_StepIndex",
                schema: "automation",
                table: "notifications",
                columns: new[] { "RunId", "StepIndex" },
                unique: true,
                filter: "\"RunId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_TaskId",
                schema: "automation",
                table: "notifications",
                column: "TaskId",
                unique: true,
                filter: "\"TaskId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId_ReadAt_CreatedAt",
                schema: "automation",
                table: "notifications",
                columns: new[] { "UserId", "ReadAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_AssigneeUserId_CompletedAt_DueAt",
                schema: "automation",
                table: "tasks",
                columns: new[] { "AssigneeUserId", "CompletedAt", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_RunId_StepIndex",
                schema: "automation",
                table: "tasks",
                columns: new[] { "RunId", "StepIndex" },
                unique: true,
                filter: "\"RunId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_receipts_Name_SignatureHash",
                schema: "automation",
                table: "webhook_receipts",
                columns: new[] { "Name", "SignatureHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_SubjectType_SubjectId",
                schema: "automation",
                table: "workflow_runs",
                columns: new[] { "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowId_CreatedAt",
                schema: "automation",
                table: "workflow_runs",
                columns: new[] { "WorkflowId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowId_EventId",
                schema: "automation",
                table: "workflow_runs",
                columns: new[] { "WorkflowId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkflowId_Number",
                schema: "automation",
                table: "workflow_versions",
                columns: new[] { "WorkflowId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflows_Trigger_Enabled",
                schema: "automation",
                table: "workflows",
                columns: new[] { "Trigger", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_logs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "tasks",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "webhook_receipts",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_versions",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_runs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflows",
                schema: "automation");
        }
    }
}
