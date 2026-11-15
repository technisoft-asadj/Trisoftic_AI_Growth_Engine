using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Ai;

public sealed class AiUsageRecord : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? TemplateKey { get; set; }
    public int TemplateVersion { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? UserId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public AiOutcome Outcome { get; set; }
    public int LatencyMs { get; set; }
    public string? CorrelationId { get; set; }
    public string? Error { get; set; }
}

public sealed class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public const string Schema = "ai";
    public DbSet<AiUsageRecord> Usage => Set<AiUsageRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<AiUsageRecord>(e =>
        {
            e.ToTable("usage_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(120);
            e.Property(x => x.Purpose).HasMaxLength(120);
            e.Property(x => x.TemplateKey).HasMaxLength(120);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.EstimatedCostUsd).HasPrecision(18, 6);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.ProductId, x.OccurredAt });
            e.HasIndex(x => x.OccurredAt);
        });
    }
}
