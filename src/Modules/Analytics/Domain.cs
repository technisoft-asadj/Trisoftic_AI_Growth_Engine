using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Analytics;

public enum MetricUnit { Count, Money, Percent, Ratio, Seconds }
/// <summary>How a metric combines over several periods. Sum: additive. Latest: a level such as followers. None: not meaningful to combine (unique reach, rates), shown per period.</summary>
public enum Aggregation { Sum, Latest, None }
public enum Direction { HigherIsBetter, LowerIsBetter, Neutral }
public enum SourceKind { Manual, Platform, Social }
public enum ObservationStatus { Measured, Unavailable }
public enum BatchStatus { Applied, RolledBack }

/// <summary>What a number means. Shown next to every figure so nobody has to guess.</summary>
public sealed class MetricDefinition : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public MetricUnit Unit { get; set; }
    public Aggregation Aggregation { get; set; }
    public Direction Direction { get; set; }
    public string Definition { get; set; } = "";
    public bool IsBuiltIn { get; set; }
}

public sealed class DataSource : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? ProductId { get; set; }
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; }
    /// <summary>How long after the last retrieval the data counts as stale.</summary>
    public int FreshnessHours { get; set; } = 48;
    public DateTimeOffset? LastRetrievedAt { get; set; }
    public DateTimeOffset? LastStaleAlertAt { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ImportBatch : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid DataSourceId { get; set; }
    public string FileName { get; set; } = "";
    public BatchStatus Status { get; set; }
    public int Rows { get; set; }
    public int Imported { get; set; }
    public int Replaced { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RolledBackAt { get; set; }
    public Guid? RolledBackBy { get; set; }
}

public sealed class MetricObservation : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid DataSourceId { get; set; }
    public Guid? ImportBatchId { get; set; }
    public string MetricKey { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    /// <summary>Null when the source could not provide the number. Never defaulted to zero.</summary>
    public decimal? Value { get; set; }
    public ObservationStatus Status { get; set; }
    /// <summary>Empty unless the metric is money (then a 3-letter code). Part of the natural key so two currencies never overwrite each other.</summary>
    public string Currency { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Market { get; set; } = "";
    /// <summary>Guid.Empty means "not tied to a campaign" (kept non-null so the natural key can be unique).</summary>
    public Guid CampaignId { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
}

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public const string Schema = "analytics";
    public DbSet<MetricDefinition> Definitions => Set<MetricDefinition>();
    public DbSet<DataSource> Sources => Set<DataSource>();
    public DbSet<ImportBatch> Batches => Set<ImportBatch>();
    public DbSet<MetricObservation> Observations => Set<MetricObservation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<MetricDefinition>(e =>
        {
            e.ToTable("metric_definitions"); e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(60); e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Category).HasMaxLength(40); e.Property(x => x.Definition).HasMaxLength(1000);
            e.Property(x => x.Unit).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Aggregation).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Key).IsUnique();
        });
        b.Entity<DataSource>(e =>
        {
            e.ToTable("data_sources"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.Name }).IsUnique();
        });
        b.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches"); e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
        b.Entity<MetricObservation>(e =>
        {
            e.ToTable("metric_observations"); e.HasKey(x => x.Id);
            e.Property(x => x.MetricKey).HasMaxLength(60); e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Channel).HasMaxLength(60); e.Property(x => x.Market).HasMaxLength(60);
            e.Property(x => x.Value).HasPrecision(20, 4);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.DataSourceId, x.MetricKey, x.PeriodStart, x.PeriodEnd, x.Channel, x.Market, x.CampaignId, x.Currency }).IsUnique();
            e.HasIndex(x => new { x.ProductId, x.MetricKey, x.PeriodStart });
            e.HasIndex(x => x.ImportBatchId);
        });
    }
}
