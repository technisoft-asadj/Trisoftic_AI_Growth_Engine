using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Messaging;

public enum MessageStatus { Queued, Sent, Blocked, Failed }

/// <summary>An email waiting to go out, or the record of what happened to it. Blocked means the consent gate said no at the moment of sending.</summary>
public sealed class EmailMessage : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? ContactId { get; set; }
    /// <summary>Only for relationship messages to someone who is not a contact (for example a booking confirmation). Marketing always goes through a contact.</summary>
    public string? ToAddress { get; set; }
    public ContactPurpose Purpose { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public MessageStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? ProviderMessageId { get; set; }
    /// <summary>Unique: asking to send the same thing twice (a retried workflow step) queues it once.</summary>
    public string DedupKey { get; set; } = "";
    public string? SubjectType { get; set; }
    public Guid? SubjectId { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public const string Schema = "messaging";
    public DbSet<EmailMessage> Emails => Set<EmailMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<EmailMessage>(e =>
        {
            e.ToTable("email_messages"); e.HasKey(x => x.Id);
            e.Property(x => x.ToAddress).HasMaxLength(320);
            e.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.ProviderMessageId).HasMaxLength(300);
            e.Property(x => x.DedupKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.SubjectType).HasMaxLength(40);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.QueuedAt });
            e.HasIndex(x => new { x.SubjectType, x.SubjectId });
        });
    }
}
