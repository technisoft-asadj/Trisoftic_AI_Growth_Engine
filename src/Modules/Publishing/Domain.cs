using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Publishing;

public enum AccountStatus { Connected, NeedsReconnect, Disconnected }
public enum AttemptStatus { Started, Succeeded, Failed, Unknown }
public enum PostSource { Provider, Manual }

/// <summary>A connected social account (a Facebook Page, a sandbox page) that a product publishes to.</summary>
public sealed class ChannelAccount : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>"facebook-page" or "sandbox".</summary>
    public string Provider { get; set; } = "";
    /// <summary>The content channel label this account serves, e.g. "Facebook". Content items are routed by (product, channel).</summary>
    public string Channel { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Encrypted with ASP.NET Data Protection. Never returned by the API, never logged.</summary>
    public string TokenCipher { get; set; } = "";
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Connected;
    public string? LastError { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public Guid? ConnectedBy { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
}

/// <summary>One try at sending a post to a provider. The row is written before the call, so a crash mid-call leaves evidence.</summary>
public sealed class PublishingAttempt : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContentItemId { get; set; }
    public int Version { get; set; }
    public Guid AccountId { get; set; }
    public int Number { get; set; }
    public AttemptStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Proof a version is live: the provider's own id. One per (item, version), which is what makes publishing idempotent.</summary>
public sealed class PublishedPost : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContentItemId { get; set; }
    public int Version { get; set; }
    public Guid ProductId { get; set; }
    public Guid? AccountId { get; set; }
    public string ProviderPostId { get; set; } = "";
    public string? Url { get; set; }
    public PostSource Source { get; set; }
    public string? Note { get; set; }
    public Guid? RecordedBy { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}

/// <summary>Between the provider's consent screen and choosing which page to connect. Short-lived; the token is encrypted.</summary>
public sealed class PendingConnection : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string PagesJsonCipher { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class PublishingDbContext(DbContextOptions<PublishingDbContext> options) : DbContext(options)
{
    public const string Schema = "publishing";
    public DbSet<ChannelAccount> Accounts => Set<ChannelAccount>();
    public DbSet<PublishingAttempt> Attempts => Set<PublishingAttempt>();
    public DbSet<PublishedPost> Posts => Set<PublishedPost>();
    public DbSet<PendingConnection> Pending => Set<PendingConnection>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<ChannelAccount>(e =>
        {
            e.ToTable("channel_accounts"); e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(40); e.Property(x => x.Channel).HasMaxLength(60);
            e.Property(x => x.ExternalId).HasMaxLength(200); e.Property(x => x.DisplayName).HasMaxLength(300);
            e.Property(x => x.TokenCipher).HasMaxLength(8000); e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProductId, x.Channel });
            e.HasIndex(x => new { x.Provider, x.ExternalId, x.ProductId }).IsUnique();
        });
        b.Entity<PublishingAttempt>(e =>
        {
            e.ToTable("publishing_attempts"); e.HasKey(x => x.Id);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ContentItemId, x.Version, x.Number }).IsUnique();
        });
        b.Entity<PublishedPost>(e =>
        {
            e.ToTable("published_posts"); e.HasKey(x => x.Id);
            e.Property(x => x.ProviderPostId).HasMaxLength(300); e.Property(x => x.Url).HasMaxLength(1000); e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ContentItemId, x.Version }).IsUnique();
            e.HasIndex(x => x.ProductId);
        });
        b.Entity<PendingConnection>(e =>
        {
            e.ToTable("pending_connections"); e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(120);
        });
    }
}
