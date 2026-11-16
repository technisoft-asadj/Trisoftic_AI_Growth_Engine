using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Assets;

public enum AssetKind { Logo, Screenshot, GameplayFootage, ProductDemo, Image, Video, Template, Document, Other }
public enum AssetSource { Own, Licensed, ThirdParty, AiGenerated }
public enum UsageRights { Unknown, InternalOnly, Unrestricted, LicensedWithLimits }

public sealed class Asset : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>All versions of the same logical asset share a group id.</summary>
    public Guid GroupId { get; set; }
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public AssetKind Kind { get; set; }
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public AssetSource Source { get; set; }
    public string? OwnerName { get; set; }
    public UsageRights Rights { get; set; }
    public string? RightsNotes { get; set; }
    public DateTimeOffset? RightsExpiresAt { get; set; }
    /// <summary>True when the file is generated/mocked and must never be presented as authentic UI or gameplay (PRD 5.6).</summary>
    public bool IsSynthetic { get; set; }
    /// <summary>Read from the file when it was uploaded (MP4 only). Null means "could not be read", never zero.</summary>
    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool IsRetired { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AssetsDbContext(DbContextOptions<AssetsDbContext> options) : DbContext(options)
{
    public const string Schema = "assets";
    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Rights).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.OwnerName).HasMaxLength(200);
            e.Property(x => x.RightsNotes).HasMaxLength(2000);
            e.HasIndex(x => new { x.ProductId, x.IsRetired });
            e.HasIndex(x => new { x.GroupId, x.Version }).IsUnique();
        });
    }
}
