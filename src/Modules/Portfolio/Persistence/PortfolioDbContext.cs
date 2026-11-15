using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.Modules.Portfolio.Domain;

namespace TriSoftic.GrowthEngine.Modules.Portfolio.Persistence;

public sealed class PortfolioDbContext(DbContextOptions<PortfolioDbContext> options) : DbContext(options)
{
    public const string Schema = "portfolio";
    public DbSet<ProductCategory> Categories => Set<ProductCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductProfileVersion> ProfileVersions => Set<ProductProfileVersion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<ProductCategory>(e =>
        {
            e.ToTable("product_categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Lifecycle).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Summary).HasMaxLength(2000);
            e.Property(x => x.Identifiers).HasColumnType("jsonb")
                .HasConversion(
                    d => System.Text.Json.JsonSerializer.Serialize(d, (System.Text.Json.JsonSerializerOptions?)null),
                    s => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(s, (System.Text.Json.JsonSerializerOptions?)null) ?? new Dictionary<string, string>(),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<Dictionary<string, string>>(
                        (a, c) => a != null && c != null && a.Count == c.Count && !a.Except(c).Any(),
                        d => d.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                        d => new Dictionary<string, string>(d)));
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasOne<ProductCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ProductProfileVersion>(e =>
        {
            e.ToTable("product_profile_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.ContentJson).HasColumnType("jsonb");
            e.Property(x => x.ChangeNote).HasMaxLength(1000);
            e.Property(x => x.DecisionComment).HasMaxLength(2000);
            e.HasIndex(x => new { x.ProductId, x.Number }).IsUnique();
            // One open (Draft/InReview) version per product at a time.
            e.HasIndex(x => x.ProductId).IsUnique().HasFilter("\"Status\" IN ('Draft','InReview')").HasDatabaseName("ux_profile_one_open_version");
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
