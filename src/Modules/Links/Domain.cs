using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Links;

/// <summary>One short link. Usually made while a post is drafted (then tied to that post); a person can also make loose ones for a bio, a message or a flyer.</summary>
public sealed class LinkRow : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>The short, unguessable part of the address.</summary>
    public string Code { get; set; } = "";
    public string Destination { get; set; } = "";
    /// <summary>Where the link is shared, for example "facebook". Becomes utm_source.</summary>
    public string Channel { get; set; } = "";
    /// <summary>Becomes utm_campaign.</summary>
    public string Campaign { get; set; } = "";
    public string? Label { get; set; }
    public Guid? ContentItemId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One visit to a short link. Holds no address and no name: only the time, whether it looks like a robot (link previews, crawlers), and a per-day one-way hash to estimate distinct visitors.</summary>
public sealed class LinkClick : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid LinkId { get; set; }
    public DateTimeOffset At { get; set; }
    public bool IsBot { get; set; }
    public string VisitorHash { get; set; } = "";
}

public sealed class LinksDbContext(DbContextOptions<LinksDbContext> options) : DbContext(options)
{
    public const string Schema = "links";
    public DbSet<LinkRow> Links => Set<LinkRow>();
    public DbSet<LinkClick> Clicks => Set<LinkClick>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<LinkRow>(e =>
        {
            e.ToTable("links"); e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(16); e.Property(x => x.Destination).HasMaxLength(2000); e.Property(x => x.Channel).HasMaxLength(60);
            e.Property(x => x.Campaign).HasMaxLength(60); e.Property(x => x.Label).HasMaxLength(200);
            e.HasIndex(x => x.Code).IsUnique(); e.HasIndex(x => new { x.ProductId, x.CreatedAt }); e.HasIndex(x => x.ContentItemId);
        });
        b.Entity<LinkClick>(e =>
        {
            e.ToTable("clicks"); e.HasKey(x => x.Id);
            e.Property(x => x.VisitorHash).HasMaxLength(32);
            e.HasIndex(x => new { x.LinkId, x.At });
        });
    }
}
