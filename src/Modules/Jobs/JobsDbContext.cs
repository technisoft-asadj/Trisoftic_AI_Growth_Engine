using Microsoft.EntityFrameworkCore;

namespace TriSoftic.GrowthEngine.Modules.Jobs;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
    public const string Schema = "jobs";
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(120).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
            e.Property(x => x.LockedBy).HasMaxLength(120);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(4000);
            e.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.RunAt });
            e.HasIndex(x => x.ProductId);
        });
    }
}
