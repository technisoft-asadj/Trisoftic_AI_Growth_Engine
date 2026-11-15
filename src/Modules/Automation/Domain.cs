using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Automation;

public enum RunStatus { Running, Waiting, Completed, Failed, Cancelled, Skipped }
public enum LogStatus { Succeeded, Skipped, Failed, Waiting }

public sealed class Workflow : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    /// <summary>Null = applies to every product.</summary>
    public Guid? ProductId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Event type that starts a run, e.g. "lead.created".</summary>
    public string Trigger { get; set; } = "";
    /// <summary>Per-workflow pause: when false it starts no new runs and waiting runs are cancelled instead of resumed.</summary>
    public bool Enabled { get; set; }
    public int CurrentVersion { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Immutable once saved: a run always executes the version that was current when it started.</summary>
public sealed class WorkflowVersion : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid WorkflowId { get; set; }
    public int Number { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public string? Note { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkflowRun : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid WorkflowId { get; set; }
    public int VersionNumber { get; set; }
    /// <summary>The triggering event. (WorkflowId, EventId) is unique, so a redelivered event cannot start a second run.</summary>
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public Guid? ProductId { get; set; }
    public string SubjectType { get; set; } = "";
    public Guid SubjectId { get; set; }
    public string EventDataJson { get; set; } = "{}";
    public RunStatus Status { get; set; }
    public int NextStep { get; set; }
    public DateTimeOffset? ResumeAt { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ActionLog : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid RunId { get; set; }
    public int StepIndex { get; set; }
    public string Kind { get; set; } = "";
    public LogStatus Status { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class WorkTask : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? ProductId { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public string? SubjectType { get; set; }
    public Guid? SubjectId { get; set; }
    /// <summary>Set when a workflow created the task. (RunId, StepIndex) is unique so a retried step cannot create it twice.</summary>
    public Guid? RunId { get; set; }
    public int? StepIndex { get; set; }
    public DateTimeOffset? OverdueNotifiedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Notification : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    public Guid? RunId { get; set; }
    public int? StepIndex { get; set; }
    /// <summary>Set for the "task overdue" alert, so one task raises one alert.</summary>
    public Guid? TaskId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Signatures of inbound webhooks already accepted. A repeated signature is a replay and is refused.</summary>
public sealed class WebhookReceipt : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string SignatureHash { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class AutomationDbContext(DbContextOptions<AutomationDbContext> options) : DbContext(options)
{
    public const string Schema = "automation";
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> Versions => Set<WorkflowVersion>();
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<ActionLog> Logs => Set<ActionLog>();
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<WebhookReceipt> Receipts => Set<WebhookReceipt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Workflow>(e =>
        {
            e.ToTable("workflows"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Trigger).HasMaxLength(80).IsRequired();
            e.HasIndex(x => new { x.Trigger, x.Enabled });
        });
        b.Entity<WorkflowVersion>(e =>
        {
            e.ToTable("workflow_versions"); e.HasKey(x => x.Id);
            e.Property(x => x.DefinitionJson).HasColumnType("jsonb");
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.WorkflowId, x.Number }).IsUnique();
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WorkflowRun>(e =>
        {
            e.ToTable("workflow_runs"); e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(80);
            e.Property(x => x.SubjectType).HasMaxLength(40);
            e.Property(x => x.EventDataJson).HasColumnType("jsonb");
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.WorkflowId, x.EventId }).IsUnique();
            e.HasIndex(x => new { x.WorkflowId, x.CreatedAt });
            e.HasIndex(x => new { x.SubjectType, x.SubjectId });
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ActionLog>(e =>
        {
            e.ToTable("action_logs"); e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.HasIndex(x => new { x.RunId, x.At });
            e.HasOne<WorkflowRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WorkTask>(e =>
        {
            e.ToTable("tasks"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.SubjectType).HasMaxLength(40);
            e.HasIndex(x => new { x.AssigneeUserId, x.CompletedAt, x.DueAt });
            e.HasIndex(x => new { x.RunId, x.StepIndex }).IsUnique().HasFilter("\"RunId\" IS NOT NULL");
        });
        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.Link).HasMaxLength(300);
            e.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
            e.HasIndex(x => new { x.RunId, x.StepIndex }).IsUnique().HasFilter("\"RunId\" IS NOT NULL");
            e.HasIndex(x => x.TaskId).IsUnique().HasFilter("\"TaskId\" IS NOT NULL");
        });
        b.Entity<WebhookReceipt>(e =>
        {
            e.ToTable("webhook_receipts"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.SignatureHash).HasMaxLength(64);
            e.HasIndex(x => new { x.Name, x.SignatureHash }).IsUnique();
        });
    }
}
