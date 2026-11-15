using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Scheduling;

public enum BookingStatus { Confirmed, Cancelled }

/// <summary>A kind of meeting (e.g. "30 min demo") with its own hours, staff and rules. Times are interpreted in <see cref="TimeZone"/>.</summary>
public sealed class AppointmentType : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public int BufferBeforeMinutes { get; set; }
    public int BufferAfterMinutes { get; set; }
    public int SlotStepMinutes { get; set; } = 30;
    public int MinNoticeHours { get; set; } = 4;
    public int MaxDaysAhead { get; set; } = 60;
    /// <summary>IANA zone the weekly hours are expressed in, e.g. "Asia/Karachi". DST is handled by the zone database.</summary>
    public string TimeZone { get; set; } = "UTC";
    /// <summary>Comma-separated staff user ids who can host this type.</summary>
    public string StaffUserIds { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public Guid[] Staff => StaffUserIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToArray();
}

public sealed class AvailabilityRule : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid AppointmentTypeId { get; set; }
    /// <summary>0 = Sunday ... 6 = Saturday (System.DayOfWeek).</summary>
    public int DayOfWeek { get; set; }
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
}

/// <summary>A whole local day with no availability (holiday, leave).</summary>
public sealed class AvailabilityException : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid AppointmentTypeId { get; set; }
    public DateOnly Date { get; set; }
    public string? Reason { get; set; }
}

public sealed class Booking : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid AppointmentTypeId { get; set; }
    public Guid ProductId { get; set; }
    public Guid StaffUserId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    /// <summary>Appointment plus buffers. The database refuses two confirmed bookings of one staff member whose blocks overlap.</summary>
    public DateTimeOffset BlockStartUtc { get; set; }
    public DateTimeOffset BlockEndUtc { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public string AttendeeName { get; set; } = "";
    public string AttendeeEmail { get; set; } = "";
    public string? AttendeePhone { get; set; }
    public string? Notes { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? OpportunityId { get; set; }
    public string? IdempotencyKey { get; set; }
    /// <summary>SHA-256 of the secret in the attendee's reschedule/cancel link. The secret itself is never stored.</summary>
    public string ManageTokenHash { get; set; } = "";
    public Guid? RescheduledFromId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public const string Schema = "scheduling";
    public DbSet<AppointmentType> Types => Set<AppointmentType>();
    public DbSet<AvailabilityRule> Rules => Set<AvailabilityRule>();
    public DbSet<AvailabilityException> Exceptions => Set<AvailabilityException>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<AppointmentType>(e =>
        {
            e.ToTable("appointment_types"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.StaffUserIds).HasMaxLength(2000);
            e.Property(x => x.PublicKey).HasMaxLength(64);
            e.Ignore(x => x.Staff);
            e.HasIndex(x => x.PublicKey).IsUnique();
            e.HasIndex(x => x.ProductId);
        });
        b.Entity<AvailabilityRule>(e =>
        {
            e.ToTable("availability_rules"); e.HasKey(x => x.Id);
            e.HasIndex(x => x.AppointmentTypeId);
            e.HasOne<AppointmentType>().WithMany().HasForeignKey(x => x.AppointmentTypeId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AvailabilityException>(e =>
        {
            e.ToTable("availability_exceptions"); e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(200);
            e.HasIndex(x => new { x.AppointmentTypeId, x.Date }).IsUnique();
            e.HasOne<AppointmentType>().WithMany().HasForeignKey(x => x.AppointmentTypeId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Booking>(e =>
        {
            e.ToTable("bookings"); e.HasKey(x => x.Id);
            e.Property(x => x.AttendeeName).HasMaxLength(200);
            e.Property(x => x.AttendeeEmail).HasMaxLength(320);
            e.Property(x => x.AttendeePhone).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Property(x => x.ManageTokenHash).HasMaxLength(64);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.HasIndex(x => x.ManageTokenHash).IsUnique();
            e.HasIndex(x => new { x.AppointmentTypeId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasIndex(x => new { x.StaffUserId, x.StartUtc });
            e.HasIndex(x => new { x.ProductId, x.StartUtc });
            e.HasIndex(x => x.LeadId);
            e.HasOne<AppointmentType>().WithMany().HasForeignKey(x => x.AppointmentTypeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
