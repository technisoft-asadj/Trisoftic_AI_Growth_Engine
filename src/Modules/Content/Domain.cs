using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Content;

public enum CampaignStatus { Planned, Active, Paused, Completed, Cancelled }

public sealed class Campaign : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public string Objective { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Market { get; set; } = "";
    public string Channels { get; set; } = "";
    public string? DestinationUrl { get; set; }
    public string? TrackingNotes { get; set; }
    public decimal? BudgetUsd { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? SuccessThresholds { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Planned;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Draft -> InReview -> Approved -> Scheduled; Publishing/Published/Failed join in Phase 3. Rejected and Cancelled end a version's life.</summary>
public enum ContentStatus { Draft, InReview, Approved, Rejected, Scheduled, Cancelled }

public sealed class ContentItem : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? CampaignId { get; set; }
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Format { get; set; } = "Post";
    public string? Language { get; set; }
    public int CurrentVersion { get; set; }
    /// <summary>Highest version that has been approved; may be lower than CurrentVersion after an edit.</summary>
    public int? ApprovedVersion { get; set; }
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public DateTimeOffset? ScheduledFor { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Any edit creates a new version, resets to Draft and drops the schedule: an approval only ever covers the exact version it was given for.</summary>
    public void AddVersion(int number)
    {
        if (Status == ContentStatus.Cancelled) throw new IllegalTransitionException("Cancelled content cannot be edited");
        CurrentVersion = number; Status = ContentStatus.Draft; ScheduledFor = null;
    }

    public void Submit()
    {
        if (Status != ContentStatus.Draft) throw new IllegalTransitionException($"Only a Draft can be submitted (was {Status})");
        Status = ContentStatus.InReview;
    }

    public void Withdraw()
    {
        if (Status != ContentStatus.InReview) throw new IllegalTransitionException($"Only InReview content can be withdrawn (was {Status})");
        Status = ContentStatus.Draft;
    }

    public void Approve(int version)
    {
        if (Status != ContentStatus.InReview || version != CurrentVersion) throw new IllegalTransitionException($"Version {version} is not the version in review");
        Status = ContentStatus.Approved; ApprovedVersion = version;
    }

    public void Reject(int version)
    {
        if (Status != ContentStatus.InReview || version != CurrentVersion) throw new IllegalTransitionException($"Version {version} is not the version in review");
        Status = ContentStatus.Rejected;
    }

    public void ReturnForChanges(int version)
    {
        if (Status != ContentStatus.InReview || version != CurrentVersion) throw new IllegalTransitionException($"Version {version} is not the version in review");
        Status = ContentStatus.Draft;
    }

    public void Schedule(DateTimeOffset at, DateTimeOffset now)
    {
        if (Status != ContentStatus.Approved || ApprovedVersion != CurrentVersion) throw new IllegalTransitionException("Only the approved, current version can be scheduled");
        if (at <= now) throw new IllegalTransitionException("The schedule time must be in the future");
        Status = ContentStatus.Scheduled; ScheduledFor = at;
    }

    public void Unschedule()
    {
        if (Status != ContentStatus.Scheduled) throw new IllegalTransitionException($"Only Scheduled content can be unscheduled (was {Status})");
        Status = ContentStatus.Approved; ScheduledFor = null;
    }

    public void Cancel()
    {
        if (Status == ContentStatus.Cancelled) throw new IllegalTransitionException("Already cancelled");
        Status = ContentStatus.Cancelled; ScheduledFor = null;
    }
}

public sealed class ContentVersion : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContentItemId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>Server-verified link to the AI draft this text started from.</summary>
    public Guid? SourceDraftId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ContentDbContext(DbContextOptions<ContentDbContext> options) : DbContext(options)
{
    public const string Schema = "content";
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<ContentItem> Items => Set<ContentItem>();
    public DbSet<ContentVersion> Versions => Set<ContentVersion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Campaign>(e =>
        {
            e.ToTable("campaigns"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Objective).HasMaxLength(2000);
            e.Property(x => x.Audience).HasMaxLength(1000);
            e.Property(x => x.Market).HasMaxLength(500);
            e.Property(x => x.Channels).HasMaxLength(500);
            e.Property(x => x.DestinationUrl).HasMaxLength(1000);
            e.Property(x => x.TrackingNotes).HasMaxLength(2000);
            e.Property(x => x.SuccessThresholds).HasMaxLength(2000);
            e.Property(x => x.BudgetUsd).HasPrecision(18, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.ProductId);
        });
        b.Entity<ContentItem>(e =>
        {
            e.ToTable("items"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Channel).HasMaxLength(60);
            e.Property(x => x.Format).HasMaxLength(40);
            e.Property(x => x.Language).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.Status });
            e.HasIndex(x => x.ScheduledFor);
            e.HasIndex(x => x.CampaignId);
        });
        b.Entity<ContentVersion>(e =>
        {
            e.ToTable("versions"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Body).HasMaxLength(60000);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.ContentItemId, x.Number }).IsUnique();
            e.HasOne<ContentItem>().WithMany().HasForeignKey(x => x.ContentItemId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
