using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Governance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION governance.audit_events_immutable() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_events is append-only';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER audit_events_no_update_delete
BEFORE UPDATE OR DELETE ON governance.audit_events
FOR EACH ROW EXECUTE FUNCTION governance.audit_events_immutable();

CREATE TRIGGER audit_events_no_truncate
BEFORE TRUNCATE ON governance.audit_events
FOR EACH STATEMENT EXECUTE FUNCTION governance.audit_events_immutable();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS audit_events_no_truncate ON governance.audit_events;
DROP TRIGGER IF EXISTS audit_events_no_update_delete ON governance.audit_events;
DROP FUNCTION IF EXISTS governance.audit_events_immutable();");
        }
    }
}
