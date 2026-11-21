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
    /// <summary>True when the autopilot created this plan and runs it (drafts, reviews, schedules). Plans made by a person are never touched by it.</summary>
    public bool Managed { get; set; }
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
    /// <summary>How many times the autopilot has drafted this slot. A rejected draft is rewritten; after the limit the slot is left for a person.</summary>
    public int Attempts { get; set; }
    /// <summary>Set when this post is one variant of a hook test (the same message opened in different ways, posted on different days).</summary>
    public Guid? TestId { get; set; }
    /// <summary>What the planner chose for this post: text, image or video. Null for plans made without visuals.</summary>
    public string? Visual { get; set; }
    /// <summary>For an image: a one-sentence description of a flat cartoon scene (no people, screens, words or logos), used when no uploaded picture is available.</summary>
    public string? ImageBrief { get; set; }
    /// <summary>The open hypothesis this post is designed to test (see the learnings register).</summary>
    public Guid? HypothesisId { get; set; }
    public string? Variant { get; set; }
    /// <summary>What the reviewer objected to last time, passed to the next draft.</summary>
    public string? Guidance { get; set; }
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

/// <summary>Per product: whether the autopilot runs, and the standing brief it plans from. Off by default.</summary>
public sealed class AutopilotSettings : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public bool Enabled { get; set; }
    public string Channel { get; set; } = "Facebook";
    public string? Language { get; set; }
    public string Goal { get; set; } = "";
    public int PlanDays { get; set; } = 7;
    public int EveryDays { get; set; } = 1;
    public TimeOnly PostTimeUtc { get; set; } = new(13, 0);
    /// <summary>Hard ceiling on posts per product, channel and UTC day, whatever the plan says.</summary>
    public int MaxPostsPerDay { get; set; } = 2;
    /// <summary>May add pictures: the product's own uploaded pictures first, AI cartoon pictures otherwise.</summary>
    public bool UseImages { get; set; } = true;
    /// <summary>May post Reels from the product's own uploaded vertical videos.</summary>
    public bool UseVideos { get; set; } = true;
    /// <summary>How far ahead posts are written and approved.</summary>
    public int LookaheadHours { get; set; } = 72;
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastRunNote { get; set; }
}

/// <summary>One decision of the AI reviewer, with every check it ran. Kept so a person can see exactly why a post went out or was held back.</summary>
public sealed class AutopilotDecision : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? PlanId { get; set; }
    public Guid? SlotId { get; set; }
    public Guid? ContentItemId { get; set; }
    public int Version { get; set; }
    /// <summary>Approved, Rejected, Escalated (left for a person) or Deferred (could not be reviewed yet).</summary>
    public string Verdict { get; set; } = "";
    public int Attempt { get; set; }
    public string Reasons { get; set; } = "";
    public string ChecksJson { get; set; } = "[]";
    public string Model { get; set; } = "";
    public decimal CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum LearningStatus { Open, Supported, Contradicted, Retired }

/// <summary>
/// One entry in a product's learnings register: a hypothesis about what works ("question openings get more comments than announcements"), what the figures later showed, and how sure we are.
/// Reviews add and judge them; every new plan must test the open ones, so the register is worked through instead of forgotten.
/// </summary>
public sealed class Learning : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public string Statement { get; set; } = "";
    public string? HowToTest { get; set; }
    public LearningStatus Status { get; set; }
    /// <summary>low | medium | high, as given by the review that last judged it.</summary>
    public string Confidence { get; set; } = "low";
    public string? Evidence { get; set; }
    public Guid? SourceReviewId { get; set; }
    public int TimesTested { get; set; }
    public Guid? LastTestedPlanId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum HookTestStatus { Running, Concluded }

/// <summary>
/// The same grounded message opened in 2-3 different ways (hook types), posted in rotation on different days, then compared. A winner is declared only with enough measured posts per variant
/// and a clear gap; "no clear winner" is a normal answer.
/// </summary>
public sealed class HookTest : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid PlanId { get; set; }
    public string Name { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Brief { get; set; } = "";
    /// <summary>JSON array of {label, hookType}.</summary>
    public string VariantsJson { get; set; } = "[]";
    public int PostsPerVariant { get; set; }
    public HookTestStatus Status { get; set; }
    public string ResultJson { get; set; } = "{}";
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConcludedAt { get; set; }
}

public sealed class PlanningDbContext(DbContextOptions<PlanningDbContext> options) : DbContext(options)
{
    public const string Schema = "planning";
    public DbSet<GrowthPlan> Plans => Set<GrowthPlan>();
    public DbSet<PlanSlot> Slots => Set<PlanSlot>();
    public DbSet<PlanReview> Reviews => Set<PlanReview>();
    public DbSet<HookTest> HookTests => Set<HookTest>();
    public DbSet<Learning> Learnings => Set<Learning>();
    public DbSet<AutopilotSettings> Autopilot => Set<AutopilotSettings>();
    public DbSet<AutopilotDecision> Decisions => Set<AutopilotDecision>();
    public DbSet<OwnerAlert> Alerts => Set<OwnerAlert>();
    public DbSet<OwnerDigest> Digests => Set<OwnerDigest>();

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
            e.Property(x => x.Rationale).HasMaxLength(8000); e.Property(x => x.Managed).HasDefaultValue(false);
            e.Property(x => x.MeasuresJson).HasColumnType("jsonb");
            e.Property(x => x.CostUsd).HasPrecision(12, 6);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
        b.Entity<PlanSlot>(e =>
        {
            e.ToTable("plan_slots"); e.HasKey(x => x.Id);
            e.Property(x => x.Angle).HasMaxLength(300); e.Property(x => x.Brief).HasMaxLength(2000); e.Property(x => x.HookType).HasMaxLength(40); e.Property(x => x.Guidance).HasMaxLength(2000); e.Property(x => x.Variant).HasMaxLength(4); e.Property(x => x.Visual).HasMaxLength(10); e.Property(x => x.ImageBrief).HasMaxLength(400); e.HasIndex(x => x.TestId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.PlanId, x.Position }).IsUnique();
            e.HasOne<GrowthPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OwnerAlert>(e =>
        {
            e.ToTable("owner_alerts"); e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(200); e.Property(x => x.Severity).HasMaxLength(10); e.Property(x => x.Message).HasMaxLength(1000);
            e.HasIndex(x => new { x.ProductId, x.ResolvedAt });
        });
        b.Entity<OwnerDigest>(e =>
        {
            e.ToTable("owner_digests"); e.HasKey(x => x.Id);
            e.Property(x => x.Subject).HasMaxLength(200); e.Property(x => x.Body).HasMaxLength(8000);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
        b.Entity<Learning>(e =>
        {
            e.ToTable("learnings"); e.HasKey(x => x.Id);
            e.Property(x => x.Statement).HasMaxLength(400); e.Property(x => x.HowToTest).HasMaxLength(600); e.Property(x => x.Evidence).HasMaxLength(2000); e.Property(x => x.Confidence).HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.Status });
        });
        b.Entity<HookTest>(e =>
        {
            e.ToTable("hook_tests"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.Channel).HasMaxLength(60); e.Property(x => x.Brief).HasMaxLength(2000);
            e.Property(x => x.VariantsJson).HasColumnType("jsonb"); e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
        b.Entity<AutopilotSettings>(e =>
        {
            e.ToTable("autopilot_settings"); e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasMaxLength(60); e.Property(x => x.Language).HasMaxLength(40); e.Property(x => x.Goal).HasMaxLength(2000); e.Property(x => x.LastRunNote).HasMaxLength(1000);
            e.Property(x => x.UseImages).HasDefaultValue(true); e.Property(x => x.UseVideos).HasDefaultValue(true);
            e.HasIndex(x => x.ProductId).IsUnique();
        });
        b.Entity<AutopilotDecision>(e =>
        {
            e.ToTable("autopilot_decisions"); e.HasKey(x => x.Id);
            e.Property(x => x.Verdict).HasMaxLength(20); e.Property(x => x.Reasons).HasMaxLength(4000); e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.ChecksJson).HasColumnType("jsonb"); e.Property(x => x.CostUsd).HasPrecision(12, 6);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
            e.HasIndex(x => x.ContentItemId);
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
