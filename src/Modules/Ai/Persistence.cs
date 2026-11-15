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

/// <summary>A saved generation result. Provenance fields are written by the server, never supplied by the client.</summary>
public sealed class AiDraft : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public string Kind { get; set; } = "";
    public string Brief { get; set; } = "";
    public string? Channel { get; set; }
    public string? Language { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string ClaimsJson { get; set; } = "[]";
    public string QuestionsJson { get; set; } = "[]";
    public int ProfileVersion { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string TemplateKey { get; set; } = "";
    public int TemplateVersion { get; set; }
    public decimal CostUsd { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public const string Schema = "ai";
    public DbSet<AiUsageRecord> Usage => Set<AiUsageRecord>();
    public DbSet<AiDraft> Drafts => Set<AiDraft>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<AiDraft>(e =>
        {
            e.ToTable("drafts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Brief).HasMaxLength(4000);
            e.Property(x => x.Channel).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.ClaimsJson).HasColumnType("jsonb");
            e.Property(x => x.QuestionsJson).HasColumnType("jsonb");
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(120);
            e.Property(x => x.TemplateKey).HasMaxLength(120);
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
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
