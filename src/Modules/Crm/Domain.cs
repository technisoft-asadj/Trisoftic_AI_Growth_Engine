using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public enum MethodType { Email, Phone, WhatsApp }
public enum ConsentStatus { Granted, Withdrawn }
public enum InteractionType { Note, Call, Email, Meeting, System }

/// <summary>A company/organization the contact belongs to. (Named Company because "OrganizationId" already means the tenant.)</summary>
public sealed class Company : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? Domain { get; set; }
    public string? Website { get; set; }
    public string? Industry { get; set; }
    public string? Notes { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Contact : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? JobTitle { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
    /// <summary>Set when this record was merged into another; the row stays as a tombstone for audit and links.</summary>
    public Guid? MergedIntoId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ContactMethod : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContactId { get; set; }
    public MethodType Type { get; set; }
    public string Value { get; set; } = "";
    public string NormalizedValue { get; set; } = "";
    public bool IsPrimary { get; set; }
}

public sealed class Tag : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ContactTag { public Guid ContactId { get; set; } public Guid TagId { get; set; } }

/// <summary>Links a contact to a product and optionally a campaign. Also drives product-scoped visibility.</summary>
public sealed class ContactAssociation : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContactId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? CampaignId { get; set; }
}

public sealed class Interaction : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContactId { get; set; }
    public InteractionType Type { get; set; }
    public string Summary { get; set; } = "";
    public Guid? ProductId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>Append-only consent history. The latest row per (contact, channel, purpose) is the current state.</summary>
public sealed class ConsentRecord : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContactId { get; set; }
    public ContactChannel Channel { get; set; }
    public ContactPurpose Purpose { get; set; }
    public ConsentStatus Status { get; set; }
    /// <summary>Where it came from: web form, import, verbal, email reply...</summary>
    public string Source { get; set; } = "";
    /// <summary>What the person agreed to (checkbox text, URL, call note). Required for grants.</summary>
    public string? Evidence { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
}

/// <summary>Address-level block list (unsubscribes, bounces, complaints). Survives merges and contact changes.</summary>
public sealed class Suppression : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public ContactChannel Channel { get; set; }
    public string NormalizedValue { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class CrmDbContext(DbContextOptions<CrmDbContext> options) : DbContext(options)
{
    public const string Schema = "crm";
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactMethod> Methods => Set<ContactMethod>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ContactTag> ContactTags => Set<ContactTag>();
    public DbSet<ContactAssociation> Associations => Set<ContactAssociation>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<ConsentRecord> Consents => Set<ConsentRecord>();
    public DbSet<Suppression> Suppressions => Set<Suppression>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Company>(e =>
        {
            e.ToTable("companies"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.Domain).HasMaxLength(255);
            e.Property(x => x.Website).HasMaxLength(500);
            e.Property(x => x.Industry).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasIndex(x => x.Domain).IsUnique().HasFilter("\"Domain\" IS NOT NULL");
            e.HasIndex(x => x.Name);
        });
        b.Entity<Contact>(e =>
        {
            e.ToTable("contacts"); e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).HasMaxLength(150);
            e.Property(x => x.LastName).HasMaxLength(150);
            e.Property(x => x.DisplayName).HasMaxLength(320).IsRequired();
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.HasIndex(x => x.CompanyId);
            e.HasIndex(x => x.MergedIntoId);
            e.HasIndex(x => x.OwnerUserId);
        });
        b.Entity<ContactMethod>(e =>
        {
            e.ToTable("contact_methods"); e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Value).HasMaxLength(320).IsRequired();
            e.Property(x => x.NormalizedValue).HasMaxLength(320).IsRequired();
            // The same email/phone can belong to only one contact: this is the hard duplicate guard.
            e.HasIndex(x => new { x.Type, x.NormalizedValue }).IsUnique();
            e.HasIndex(x => x.ContactId);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Tag>(e => { e.ToTable("tags"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(60).IsRequired(); e.HasIndex(x => x.Name).IsUnique(); });
        b.Entity<ContactTag>(e =>
        {
            e.ToTable("contact_tags"); e.HasKey(x => new { x.ContactId, x.TagId });
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ContactAssociation>(e =>
        {
            e.ToTable("contact_associations"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ContactId, x.ProductId, x.CampaignId }).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Interaction>(e =>
        {
            e.ToTable("interactions"); e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Summary).HasMaxLength(4000).IsRequired();
            e.HasIndex(x => new { x.ContactId, x.OccurredAt });
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ConsentRecord>(e =>
        {
            e.ToTable("consent_records"); e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Source).HasMaxLength(200).IsRequired();
            e.Property(x => x.Evidence).HasMaxLength(2000);
            e.HasIndex(x => new { x.ContactId, x.Channel, x.Purpose, x.RecordedAt });
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Suppression>(e =>
        {
            e.ToTable("suppressions"); e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.NormalizedValue).HasMaxLength(320).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.Channel, x.NormalizedValue }).IsUnique();
        });
    }
}
