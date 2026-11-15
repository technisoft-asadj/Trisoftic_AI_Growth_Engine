using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Budget;

public enum BudgetScope { Company, Product, Campaign }
public enum RiskTolerance { Conservative, Balanced, Growth }
public enum BudgetStatus { Draft, Approved, Closed }
/// <summary>
/// The four states a figure can be in. They are kept as separate records and never merged into one number:
/// Recommended (a suggestion, costs nothing), Approved (authorised to spend), Committed (promised, e.g. an ad order), Actual (money has left).
/// </summary>
public enum SpendKind { Recommended, Approved, Committed, Actual }
public enum ChangeKind { Launch, Increase, Reallocation }
public enum ChangeStatus { Pending, Applied, Rejected, Withdrawn }
public enum AlertLevel { None, Warning, Stop }

public sealed class MarketingBudget : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public BudgetScope Scope { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? CampaignId { get; set; }
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    /// <summary>The ceiling. While Draft it is a proposal; once Approved it changes only through an approved Increase.</summary>
    public decimal HardCap { get; set; }
    public RiskTolerance Risk { get; set; } = RiskTolerance.Balanced;
    public int WarnAtPct { get; set; } = 80;
    public int StopAtPct { get; set; } = 100;
    public AlertLevel LastAlert { get; set; }
    public BudgetStatus Status { get; set; }
    /// <summary>Bumped on every approved change; a pending change built on an older version is refused.</summary>
    public int Version { get; set; } = 1;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BudgetAllocation : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid BudgetId { get; set; }
    /// <summary>Usually a channel or campaign name, e.g. "Facebook ads".</summary>
    public string Label { get; set; } = "";
    public decimal Amount { get; set; }
    public int SortOrder { get; set; }
}

public sealed class SpendRecord : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid BudgetId { get; set; }
    public Guid? AllocationId { get; set; }
    public SpendKind Kind { get; set; }
    public decimal Amount { get; set; }
    public DateOnly OccurredOn { get; set; }
    public string? Note { get; set; }
    /// <summary>"manual" or "planner".</summary>
    public string Source { get; set; } = "manual";
    public bool Voided { get; set; }
    public string? VoidReason { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A request to launch a budget, raise its cap, or move money between allocations. Immutable once made: the approval covers exactly these amounts.</summary>
public sealed class BudgetChange : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid BudgetId { get; set; }
    public ChangeKind Kind { get; set; }
    /// <summary>The budget version this change was built on.</summary>
    public int BaseVersion { get; set; }
    public decimal CurrentCap { get; set; }
    public decimal ProposedCap { get; set; }
    /// <summary>The allocation list as it would be after the change (JSON array of {label, amount}).</summary>
    public string ProposedAllocationsJson { get; set; } = "[]";
    public string Reason { get; set; } = "";
    public ChangeStatus Status { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}

public sealed class BudgetPlan : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid BudgetId { get; set; }
    /// <summary>"Forecast" when there is enough history, otherwise "LearningExperiment".</summary>
    public string Mode { get; set; } = "";
    public string InputsJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BudgetDbContext(DbContextOptions<BudgetDbContext> options) : DbContext(options)
{
    public const string Schema = "budget";
    public DbSet<MarketingBudget> Budgets => Set<MarketingBudget>();
    public DbSet<BudgetAllocation> Allocations => Set<BudgetAllocation>();
    public DbSet<SpendRecord> Spend => Set<SpendRecord>();
    public DbSet<BudgetChange> Changes => Set<BudgetChange>();
    public DbSet<BudgetPlan> Plans => Set<BudgetPlan>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<MarketingBudget>(e =>
        {
            e.ToTable("budgets"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.HardCap).HasPrecision(18, 2);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Risk).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastAlert).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.Status });
        });
        b.Entity<BudgetAllocation>(e =>
        {
            e.ToTable("allocations"); e.HasKey(x => x.Id);
            e.Property(x => x.Label).HasMaxLength(200); e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasIndex(x => x.BudgetId);
            e.HasOne<MarketingBudget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SpendRecord>(e =>
        {
            e.ToTable("spend_records"); e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Note).HasMaxLength(500); e.Property(x => x.Source).HasMaxLength(20); e.Property(x => x.VoidReason).HasMaxLength(500);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.BudgetId, x.Kind });
            e.HasOne<MarketingBudget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<BudgetChange>(e =>
        {
            e.ToTable("changes"); e.HasKey(x => x.Id);
            e.Property(x => x.CurrentCap).HasPrecision(18, 2); e.Property(x => x.ProposedCap).HasPrecision(18, 2);
            e.Property(x => x.ProposedAllocationsJson).HasColumnType("jsonb"); e.Property(x => x.Reason).HasMaxLength(1000); e.Property(x => x.DecisionNote).HasMaxLength(1000);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.BudgetId, x.Status });
            e.HasOne<MarketingBudget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<BudgetPlan>(e =>
        {
            e.ToTable("plans"); e.HasKey(x => x.Id);
            e.Property(x => x.Mode).HasMaxLength(30);
            e.Property(x => x.InputsJson).HasColumnType("jsonb"); e.Property(x => x.OutputJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.BudgetId, x.CreatedAt });
            e.HasOne<MarketingBudget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
