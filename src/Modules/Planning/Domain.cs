using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Planning;

public enum PlanStatus { Draft, Active, Completed, Cancelled }
/// <summary>Planned: only an angle and a brief. Drafting: a post is being written right now. Drafted: a post exists (in the content module) and follows the normal approval flow.</summary>
public enum SlotStatus { Planned, Drafting, Drafted }

/// <summary>
/// A time-boxed plan for one product and channel (3 days, a week, a month): what to say on which day and why, and how to tell afterwards whether it worked.
/// The plan only organises the work. Every post it produces is an ordinary content item that needs its own approval before anything is published.
/// </summary>
public sealed class GrowthPlan : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Channel { get; set; } = "Facebook";
    public string? Language { get; set; }
    public DateOnly StartDate { get; set; }
    public int Days { get; set; }
    public int EveryDays { get; set; } = 1;
    /// <summary>Time of day (UTC) each post goes out.</summary>
    public TimeOnly PostTimeUtc { get; set; }
    public PlanStatus Status { get; set; }
    /// <summary>Why this mix of posts: the planner's reasoning, shown to the person approving the plan.</summary>
    public string Rationale { get; set; } = "";
    /// <summary>What will be looked at afterwards to judge the plan (JSON array of {name, why}).</summary>
    public string MeasuresJson { get; set; } = "[]";
    /// <summary>Set when this plan was built from the learnings of an earlier plan's review.</summary>
    public Guid? PreviousPlanId { get; set; }
    public Guid? PreviousReviewId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public decimal CostUsd { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PlanSlot : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }
    public int Position { get; set; }
    public DateTimeOffset ScheduledFor { get; set; }
    public string Angle { get; set; } = "";
    public string Brief { get; set; } = "";
    /// <summary>How this post should open (see HookTypes). Carried onto the content item when the post is drafted.</summary>
    public string? HookType { get; set; }
    public SlotStatus Status { get; set; }
    public Guid? ContentItemId { get; set; }
    public DateTimeOffset? DraftedAt { get; set; }
}

/// <summary>An after-the-fact look at how a plan's posts did, and what to do next. Never invents numbers: figures the network did not supply are stored as missing.</summary>
public sealed class PlanReview : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }
    /// <summary>How much evidence there was (published / measured posts), stored so the review can be judged later.</summary>
    public string DataQualityJson { get; set; } = "{}";
    /// <summary>Per-post figures exactly as they were when the review was made.</summary>
    public string FiguresJson { get; set; } = "[]";
    /// <summary>The analysis: headline, what worked, what did not, learnings, next plan suggestion, caveats.</summary>
    public string ResultJson { get; set; } = "{}";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public decimal CostUsd { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlanningDbContext(DbContextOptions<PlanningDbContext> options) : DbContext(options)
{
    public const string Schema = "planning";
    public DbSet<GrowthPlan> Plans => Set<GrowthPlan>();
    public DbSet<PlanSlot> Slots => Set<PlanSlot>();
    public DbSet<PlanReview> Reviews => Set<PlanReview>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<GrowthPlan>(e =>
        {
            e.ToTable("plans"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Goal).HasMaxLength(2000);
            e.Property(x => x.Channel).HasMaxLength(60); e.Property(x => x.Language).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Rationale).HasMaxLength(8000);
            e.Property(x => x.MeasuresJson).HasColumnType("jsonb");
            e.Property(x => x.CostUsd).HasPrecision(12, 6);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
        b.Entity<PlanSlot>(e =>
        {
            e.ToTable("plan_slots"); e.HasKey(x => x.Id);
            e.Property(x => x.Angle).HasMaxLength(300); e.Property(x => x.Brief).HasMaxLength(2000); e.Property(x => x.HookType).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.PlanId, x.Position }).IsUnique();
            e.HasOne<GrowthPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PlanReview>(e =>
        {
            e.ToTable("plan_reviews"); e.HasKey(x => x.Id);
            e.Property(x => x.DataQualityJson).HasColumnType("jsonb"); e.Property(x => x.FiguresJson).HasColumnType("jsonb"); e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.CostUsd).HasPrecision(12, 6);
            e.HasIndex(x => new { x.PlanId, x.CreatedAt });
            e.HasOne<GrowthPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
