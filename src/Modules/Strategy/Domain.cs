using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Strategy;

public sealed class Kpi { public string Name { get; set; } = ""; public string? Target { get; set; } }
public sealed class Hypothesis { public string Statement { get; set; } = ""; public string? Evidence { get; set; } public string Status { get; set; } = "Open"; }

/// <summary>PRD 5.4: objective, audience, market, language, channel, positioning, message, CTA, duration, cadence, budget, KPIs, hypotheses, assumptions, risks.</summary>
public sealed class StrategyContent
{
    public string Objective { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Market { get; set; } = "";
    public string Language { get; set; } = "";
    public List<string> Channels { get; set; } = [];
    public string Positioning { get; set; } = "";
    public List<string> KeyMessages { get; set; } = [];
    public string Cta { get; set; } = "";
    public int? DurationDays { get; set; }
    public string Cadence { get; set; } = "";
    public decimal? BudgetUsd { get; set; }
    public List<Kpi> Kpis { get; set; } = [];
    public List<Hypothesis> Hypotheses { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    /// <summary>Free-form narrative (for example an adopted AI strategy brief).</summary>
    public string Notes { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static StrategyContent FromJson(string j) => JsonSerializer.Deserialize<StrategyContent>(j, Json) ?? new();

    public List<string> Validate()
    {
        var e = new List<string>();
        if (BudgetUsd is < 0) e.Add("Budget cannot be negative.");
        if (DurationDays is < 1 or > 1095) e.Add("Duration must be between 1 and 1095 days.");
        if (Objective.Length > 2000 || Positioning.Length > 2000 || Notes.Length > 40000) e.Add("A text field is too long.");
        if (Channels.Count > 20 || KeyMessages.Count > 50 || Kpis.Count > 50 || Hypotheses.Count > 100 || Assumptions.Count > 100 || Risks.Count > 100) e.Add("Too many list entries.");
        if (Hypotheses.Any(h => string.IsNullOrWhiteSpace(h.Statement))) e.Add("Every hypothesis needs a statement.");
        if (Hypotheses.Any(h => h.Status is not ("Open" or "Supported" or "Refuted"))) e.Add("Hypothesis status must be Open, Supported or Refuted.");
        if (Kpis.Any(k => string.IsNullOrWhiteSpace(k.Name))) e.Add("Every KPI needs a name.");
        return e;
    }
}

public static class StrategyTemplates
{
    public sealed record Template(string Key, string Name, string Description, StrategyContent Starter);

    public static readonly IReadOnlyList<Template> All =
    [
        new("b2c-gaming", "B2C gaming", "Consumer mobile game: installs, retention and monetization.", new StrategyContent
        {
            Objective = "", Cadence = "3 posts per week",
            Kpis = [new() { Name = "Store listing visits" }, new() { Name = "Installs" }, new() { Name = "Day-1 retention" }, new() { Name = "Day-7 retention" }, new() { Name = "Cost per install" }, new() { Name = "ROAS" }],
            Hypotheses = [new() { Statement = "Short gameplay clips convert better to installs than static images." }],
            Assumptions = ["Gameplay analytics events are instrumented and trustworthy before any paid acquisition."],
            Risks = ["Paid acquisition scaled before retention and monetization are understood."],
        }),
        new("b2b-software", "B2B software", "Business software: leads, demos and pipeline.", new StrategyContent
        {
            Objective = "", Cadence = "2 posts per week + 1 landing page",
            Kpis = [new() { Name = "Landing page conversion" }, new() { Name = "Enquiries" }, new() { Name = "Qualified leads" }, new() { Name = "Demos booked" }, new() { Name = "Cost per lead" }, new() { Name = "Opportunity conversion" }],
            Hypotheses = [new() { Statement = "Industry-specific messaging produces more qualified enquiries than generic messaging." }],
            Assumptions = ["Features, pricing and differentiators have been verified before being used in public content."],
            Risks = ["Unsolicited follow-up without consent."],
        }),
        new("blank", "Blank", "Start from an empty strategy.", new StrategyContent()),
    ];
}

public sealed class StrategyDoc : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public string TemplateKey { get; set; } = "blank";
    public int CurrentVersion { get; set; }
    public bool IsArchived { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StrategyVersion : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid StrategyId { get; set; }
    public int Number { get; set; }
    public string ContentJson { get; set; } = "{}";
    public string? Note { get; set; }
    /// <summary>Set only by the server after verifying the draft exists and belongs to this product.</summary>
    public Guid? SourceDraftId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StrategyDbContext(DbContextOptions<StrategyDbContext> options) : DbContext(options)
{
    public const string Schema = "strategy";
    public DbSet<StrategyDoc> Strategies => Set<StrategyDoc>();
    public DbSet<StrategyVersion> Versions => Set<StrategyVersion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<StrategyDoc>(e =>
        {
            e.ToTable("strategies"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TemplateKey).HasMaxLength(40);
            e.HasIndex(x => new { x.ProductId, x.IsArchived });
        });
        b.Entity<StrategyVersion>(e =>
        {
            e.ToTable("strategy_versions"); e.HasKey(x => x.Id);
            e.Property(x => x.ContentJson).HasColumnType("jsonb");
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.StrategyId, x.Number }).IsUnique();
            e.HasOne<StrategyDoc>().WithMany().HasForeignKey(x => x.StrategyId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
