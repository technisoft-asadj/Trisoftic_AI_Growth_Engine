using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.Modules.Governance.Approvals;
using TriSoftic.GrowthEngine.Modules.Governance.Audit;
using TriSoftic.GrowthEngine.Modules.Governance.Pause;

namespace TriSoftic.GrowthEngine.Modules.Governance.Persistence;

public sealed class GovernanceDbContext(DbContextOptions<GovernanceDbContext> options) : DbContext(options)
{
    public const string Schema = "governance";
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<PauseFlag> PauseFlags => Set<PauseFlag>();
    public DbSet<ApprovalRecord> Approvals => Set<ApprovalRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(120).IsRequired();
            e.Property(x => x.SubjectType).HasMaxLength(120).IsRequired();
            e.Property(x => x.SubjectId).HasMaxLength(120);
            e.Property(x => x.ActorEmail).HasMaxLength(320);
            e.Property(x => x.Summary).HasMaxLength(2000);
            e.Property(x => x.BeforeJson).HasColumnType("jsonb");
            e.Property(x => x.AfterJson).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.SubjectType, x.SubjectId });
            e.HasIndex(x => x.ProductId);
        });
        b.Entity<ApprovalRecord>(e =>
        {
            e.ToTable("approvals");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectType).HasMaxLength(120).IsRequired();
            e.Property(x => x.SubjectId).HasMaxLength(120).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(2000);
            e.Property(x => x.DecisionComment).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasIndex(x => new { x.SubjectType, x.SubjectId, x.SubjectVersion });
            e.HasIndex(x => new { x.Status, x.ProductId });
            // At most one Pending approval per subject.
            e.HasIndex(x => new { x.SubjectType, x.SubjectId }).IsUnique().HasFilter("\"Status\" = 'Pending'");
        });
        b.Entity<PauseFlag>(e =>
        {
            e.ToTable("pause_flags");
            e.HasKey(x => x.Scope);
            e.Property(x => x.Scope).HasMaxLength(40);
            e.Property(x => x.Reason).HasMaxLength(1000);
        });
    }
}
