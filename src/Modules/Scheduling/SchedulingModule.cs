using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Scheduling;

public static partial class SchedulingModule
{
    /// <summary>Rate-limit policy the host must register for the anonymous booking routes.</summary>
    public const string PublicPolicy = "public-booking";

    public sealed record TypeIn(string Name, string? Description, int DurationMinutes, int? BufferBeforeMinutes, int? BufferAfterMinutes, int? SlotStepMinutes,
        int? MinNoticeHours, int? MaxDaysAhead, string TimeZone, Guid[] StaffUserIds, bool? Enabled);
    public sealed record RuleIn(int DayOfWeek, string Start, string End);
    public sealed record AvailabilityIn(RuleIn[] Rules, DateOnly[]? ClosedDates);
    public sealed record InternalBookIn(Guid AppointmentTypeId, DateTimeOffset Start, string Name, string Email, string? Phone, string? Notes, Guid? LeadId, Guid? OpportunityId, string? IdempotencyKey);
    public sealed record PublicBookIn(DateTimeOffset Start, string Name, string Email, string? Phone, string? Notes, string? Ref, string? IdempotencyKey);
    public sealed record CancelIn(string? Reason);
    public sealed record RescheduleIn(DateTimeOffset Start);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRx();

    public static IServiceCollection AddSchedulingModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<SchedulingDbContext>(c, SchedulingDbContext.Schema);
        s.AddScoped<ISubjectStateProvider, BookingStateProvider>();
        // The booking page is embeddable and anonymous: no credentials, simple requests only.
        s.AddCors(o => o.AddPolicy(PublicPolicy, p => p.AllowAnyOrigin().WithMethods("GET", "POST").WithHeaders("Content-Type")));
        return s;
    }

    // ---------- helpers ----------
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Signed reference that ties an anonymous booking to a lead. Only links minted by staff verify.</summary>
    public static string SignLeadRef(Guid leadId, string secret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("lead:" + leadId.ToString("N")));
        return leadId.ToString("N") + "." + Convert.ToBase64String(mac, 0, 16).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static Guid? VerifyLeadRef(string? reference, string? secret)
    {
        if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(secret)) return null;
        var i = reference.IndexOf('.');
        if (i != 32 || !Guid.TryParseExact(reference[..32], "N", out var id)) return null;
        var expected = SignLeadRef(id, secret);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(reference)) ? id : null;
    }

    private static TimeZoneInfo? Zone(string id) { try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { return null; } }
    private static SlotSpec Spec(AppointmentType t, TimeZoneInfo z) => new(z, t.DurationMinutes, t.BufferBeforeMinutes, t.BufferAfterMinutes, t.SlotStepMinutes, t.MinNoticeHours, t.MaxDaysAhead);

    private static object TypeDto(AppointmentType t) => new
    {
        t.Id, t.ProductId, t.Name, t.Description, t.DurationMinutes, t.BufferBeforeMinutes, t.BufferAfterMinutes, t.SlotStepMinutes, t.MinNoticeHours, t.MaxDaysAhead,
        t.TimeZone, staffUserIds = t.Staff, t.PublicKey, t.Enabled,
    };

    private static object BookingDto(Booking b) => new
    {
        b.Id, b.AppointmentTypeId, b.ProductId, b.StaffUserId, start = b.StartUtc, end = b.EndUtc, b.Status, b.AttendeeName, b.AttendeeEmail, b.AttendeePhone, b.Notes,
        b.LeadId, b.OpportunityId, b.CancelReason, b.CancelledAt, b.CreatedAt, b.RescheduledFromId,
    };

    private static async Task<(List<Slot> Slots, AppointmentType Type)> SlotsAsync(SchedulingDbContext db, AppointmentType t, DateTimeOffset now, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var zone = Zone(t.TimeZone) ?? TimeZoneInfo.Utc;
        var rules = await db.Rules.AsNoTracking().Where(r => r.AppointmentTypeId == t.Id).Select(r => new WeeklyWindow(r.DayOfWeek, r.StartMinute, r.EndMinute)).ToListAsync(ct);
        var closed = (await db.Exceptions.AsNoTracking().Where(e => e.AppointmentTypeId == t.Id).Select(e => e.Date).ToListAsync(ct)).ToHashSet();
        var staff = t.Staff;
        var lo = from.AddDays(-1); var hi = to.AddDays(1);
        // Staff may host several appointment types, so busy time is read across all types.
        var busy = await db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatus.Confirmed && staff.Contains(b.StaffUserId) && b.BlockEndUtc > lo && b.BlockStartUtc < hi)
            .Select(b => new BusyBlock(b.StaffUserId, b.BlockStartUtc, b.BlockEndUtc)).ToListAsync(ct);
        return (SlotGenerator.Generate(Spec(t, zone), rules, closed, staff, busy, now, from, to), t);
    }

    /// <summary>True if Postgres reported this SQLSTATE anywhere in the chain. EF wraps "transient" errors such as deadlocks in an InvalidOperationException, so the top exception alone is not enough.</summary>
    private static bool IsViolation(Exception ex, string sqlState)
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is PostgresException pg && pg.SqlState == sqlState) return true;
        return false;
    }

    /// <summary>
    /// Places a booking on the first free staff member. The exclusion constraint in the database is the real guard: if two requests race
    /// for the same slot, exactly one insert succeeds and the others fall through to the next staff member or fail with a conflict.
    /// </summary>
    private static async Task<(Booking? Booking, string? Error)> PlaceAsync(SchedulingDbContext db, AppointmentType t, DateTimeOffset start, Booking template, Booking? replacing,
        DateTimeOffset now, CancellationToken ct)
    {
        var (slots, _) = await SlotsAsync(db, t, now, start, start.AddMinutes(1), ct);
        var slot = slots.FirstOrDefault(s => s.Start == start);
        // When rescheduling, the booking being replaced frees its own time.
        if (slot is null && replacing is not null)
        {
            var (again, _) = await SlotsExcludingAsync(db, t, replacing.Id, now, start, ct);
            slot = again.FirstOrDefault(s => s.Start == start);
        }
        if (slot is null) return (null, "That time is no longer available");

        var load = await db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatus.Confirmed && b.StartUtc >= now && b.StartUtc < now.AddDays(30) && slot.FreeStaff.Contains(b.StaffUserId))
            .GroupBy(b => b.StaffUserId).Select(g => new { Staff = g.Key, N = g.Count() }).ToListAsync(ct);
        var ordered = slot.FreeStaff.OrderBy(sid => replacing?.StaffUserId == sid ? 0 : 1).ThenBy(sid => load.FirstOrDefault(l => l.Staff == sid)?.N ?? 0).ToList();

        foreach (var staffId in ordered)
        {
            var b = new Booking
            {
                AppointmentTypeId = t.Id, ProductId = t.ProductId, StaffUserId = staffId, StartUtc = slot.Start, EndUtc = slot.End,
                BlockStartUtc = slot.Start.AddMinutes(-t.BufferBeforeMinutes), BlockEndUtc = slot.End.AddMinutes(t.BufferAfterMinutes),
                AttendeeName = template.AttendeeName, AttendeeEmail = template.AttendeeEmail, AttendeePhone = template.AttendeePhone, Notes = template.Notes,
                LeadId = template.LeadId, OpportunityId = template.OpportunityId, IdempotencyKey = template.IdempotencyKey, ManageTokenHash = template.ManageTokenHash,
                RescheduledFromId = replacing?.Id, CreatedBy = template.CreatedBy, CreatedAt = now,
            };
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Racing requests for one staff member queue here instead of colliding inside the constraint check (which can deadlock).
                // The lock is released with the transaction. The exclusion constraint below still decides who wins; this only keeps the losers orderly.
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", [staffId.ToString()], ct);
                if (replacing is not null)
                {
                    var old = await db.Bookings.FirstAsync(x => x.Id == replacing.Id, ct);
                    old.Status = BookingStatus.Cancelled; old.CancelledAt = now; old.CancelReason = "Rescheduled";
                    await db.SaveChangesAsync(ct);
                }
                db.Bookings.Add(b);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return (b, null);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException && (IsViolation(ex, PostgresErrorCodes.ExclusionViolation) || IsViolation(ex, PostgresErrorCodes.DeadlockDetected) || IsViolation(ex, PostgresErrorCodes.SerializationFailure)))
            {
                // Someone else took this staff member's time (racing inserts on the exclusion constraint can surface as a deadlock abort
                // instead of a violation; either way this attempt lost). Try the next staff member.
                await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            }
        }
        return (null, "That time is no longer available");
    }

    private static async Task<(List<Slot> Slots, AppointmentType Type)> SlotsExcludingAsync(SchedulingDbContext db, AppointmentType t, Guid ignoreBooking, DateTimeOffset now, DateTimeOffset start, CancellationToken ct)
    {
        var zone = Zone(t.TimeZone) ?? TimeZoneInfo.Utc;
        var rules = await db.Rules.AsNoTracking().Where(r => r.AppointmentTypeId == t.Id).Select(r => new WeeklyWindow(r.DayOfWeek, r.StartMinute, r.EndMinute)).ToListAsync(ct);
        var closed = (await db.Exceptions.AsNoTracking().Where(e => e.AppointmentTypeId == t.Id).Select(e => e.Date).ToListAsync(ct)).ToHashSet();
        var staff = t.Staff;
        var busy = await db.Bookings.AsNoTracking().Where(b => b.Id != ignoreBooking && b.Status == BookingStatus.Confirmed && staff.Contains(b.StaffUserId) && b.BlockEndUtc > start.AddDays(-1) && b.BlockStartUtc < start.AddDays(1))
            .Select(b => new BusyBlock(b.StaffUserId, b.BlockStartUtc, b.BlockEndUtc)).ToListAsync(ct);
        return (SlotGenerator.Generate(Spec(t, zone), rules, closed, staff, busy, now, start, start.AddMinutes(1)), t);
    }

    private static string? ValidateAttendee(string? name, string? email, string? phone, string? notes)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) return "A name is required";
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || !EmailRx().IsMatch(email.Trim())) return "A valid email address is required";
        if (phone is { Length: > 50 }) return "The phone number is too long";
        if (notes is { Length: > 2000 }) return "The notes are too long";
        return null;
    }

    // ---------- endpoints ----------
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdmin(app.MapGroup("/api/v1/scheduling").RequireAuthorization().WithTags("Scheduling"));
        MapPublic(app.MapGroup("/api/v1/public/booking").AllowAnonymous().RequireCors(PublicPolicy).RequireRateLimiting(PublicPolicy).WithTags("Public booking"));
        return app;
    }

    private static void MapAdmin(RouteGroupBuilder g)
    {
        g.MapGet("/products/{productId:guid}/appointment-types", async (Guid productId, SchedulingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            return Results.Ok((await db.Types.AsNoTracking().Where(t => t.ProductId == productId).OrderBy(t => t.Name).ToListAsync(ct)).Select(TypeDto));
        });

        g.MapPost("/products/{productId:guid}/appointment-types", async (Guid productId, TypeIn req, SchedulingDbContext db, ICurrentUser u, IUserDirectory dir, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AppointmentsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            if (await ValidateType(req, dir, ct) is { } err) return Results.Problem(statusCode: 400, title: err);
            var t = new AppointmentType { ProductId = productId, PublicKey = NewToken(), CreatedAt = clock.UtcNow };
            Apply(t, req);
            db.Types.Add(t);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("scheduling.type.created", "AppointmentType", t.Id.ToString(), productId, t.Name), ct);
            return Results.Created($"/api/v1/scheduling/appointment-types/{t.Id}", TypeDto(t));
        });

        g.MapPut("/appointment-types/{id:guid}", async (Guid id, TypeIn req, SchedulingDbContext db, ICurrentUser u, IUserDirectory dir, IAuditWriter audit, CancellationToken ct) =>
        {
            var t = await db.Types.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null || !u.HasPermission(Permissions.AppointmentsManage) || !await u.CanAccessProductAsync(t.ProductId, ct)) return Results.NotFound();
            if (await ValidateType(req, dir, ct) is { } err) return Results.Problem(statusCode: 400, title: err);
            Apply(t, req);
            await db.SaveChangesAsync(ct);   // existing bookings keep the times they were made with
            await audit.WriteAsync(new AuditEntry("scheduling.type.updated", "AppointmentType", id.ToString(), t.ProductId, t.Name), ct);
            return Results.Ok(TypeDto(t));
        });

        g.MapGet("/appointment-types/{id:guid}/availability", async (Guid id, SchedulingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var t = await db.Types.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null || !u.HasPermission(Permissions.CrmRead) || !await u.CanAccessProductAsync(t.ProductId, ct)) return Results.NotFound();
            var rules = await db.Rules.AsNoTracking().Where(r => r.AppointmentTypeId == id).OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartMinute).ToListAsync(ct);
            var ex = await db.Exceptions.AsNoTracking().Where(e => e.AppointmentTypeId == id).OrderBy(e => e.Date).ToListAsync(ct);
            string Hm(int m) => m == 1440 ? "24:00" : $"{m / 60:00}:{m % 60:00}";
            return Results.Ok(new { rules = rules.Select(r => new { r.DayOfWeek, start = Hm(r.StartMinute), end = Hm(r.EndMinute) }), closedDates = ex.Select(e => e.Date) });
        });

        // Replaces the weekly hours and closed days in one step.
        g.MapPut("/appointment-types/{id:guid}/availability", async (Guid id, AvailabilityIn req, SchedulingDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var t = await db.Types.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null || !u.HasPermission(Permissions.AppointmentsManage) || !await u.CanAccessProductAsync(t.ProductId, ct)) return Results.NotFound();
            if (req.Rules.Length > 50) return Results.Problem(statusCode: 400, title: "At most 50 weekly windows");
            var parsed = new List<AvailabilityRule>();
            foreach (var r in req.Rules)
            {
                var s = SlotGenerator.ParseMinute(r.Start); var e = SlotGenerator.ParseMinute(r.End);
                if (r.DayOfWeek is < 0 or > 6 || s is null || e is null || e <= s) return Results.Problem(statusCode: 400, title: "Each window needs a day 0-6 and a start before its end (HH:mm)");
                parsed.Add(new AvailabilityRule { AppointmentTypeId = id, DayOfWeek = r.DayOfWeek, StartMinute = s.Value, EndMinute = e.Value });
            }
            db.Rules.RemoveRange(db.Rules.Where(r => r.AppointmentTypeId == id));
            db.Exceptions.RemoveRange(db.Exceptions.Where(x => x.AppointmentTypeId == id));
            db.Rules.AddRange(parsed);
            db.Exceptions.AddRange((req.ClosedDates ?? []).Distinct().Select(d => new AvailabilityException { AppointmentTypeId = id, Date = d }));
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("scheduling.availability.updated", "AppointmentType", id.ToString(), t.ProductId, t.Name, After: new { Windows = parsed.Count, Closed = req.ClosedDates?.Length ?? 0 }), ct);
            return Results.NoContent();
        });

        g.MapGet("/appointment-types/{id:guid}/slots", async (Guid id, DateTimeOffset? from, DateTimeOffset? to, SchedulingDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var t = await db.Types.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null || !u.HasPermission(Permissions.CrmRead) || !await u.CanAccessProductAsync(t.ProductId, ct)) return Results.NotFound();
            var now = clock.UtcNow; var f = from ?? now; var e = to ?? f.AddDays(14);
            if (e <= f || e - f > TimeSpan.FromDays(62)) return Results.Problem(statusCode: 400, title: "Ask for up to 62 days");
            var (slots, _) = await SlotsAsync(db, t, now, f, e, ct);
            return Results.Ok(slots.Select(s => new { start = s.Start, end = s.End, freeStaff = s.FreeStaff.Length }));
        });

        // A link staff can put in an email to a lead; booking through it attaches the booking to that lead.
        g.MapGet("/appointment-types/{id:guid}/link", async (Guid id, Guid leadId, SchedulingDbContext db, ICurrentUser u, IConfiguration cfg, CancellationToken ct) =>
        {
            var t = await db.Types.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null || !u.HasPermission(Permissions.AppointmentsManage) || !await u.CanAccessProductAsync(t.ProductId, ct)) return Results.NotFound();
            var secret = cfg["Scheduling:LinkSecret"];
            if (string.IsNullOrEmpty(secret)) return Results.Problem(statusCode: 503, title: "Scheduling:LinkSecret is not configured");
            return Results.Ok(new { publicKey = t.PublicKey, @ref = SignLeadRef(leadId, secret), path = $"/book/{t.PublicKey}?ref={SignLeadRef(leadId, secret)}" });
        });

        g.MapGet("/bookings", async (SchedulingDbContext db, ICurrentUser u, IClock clock, Guid? productId, Guid? leadId, DateTimeOffset? from, DateTimeOffset? to, bool? includeCancelled, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var q = db.Bookings.AsNoTracking().AsQueryable();
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(b => scope.Contains(b.ProductId));
            if (productId is not null) q = q.Where(b => b.ProductId == productId);
            if (leadId is not null) q = q.Where(b => b.LeadId == leadId);
            var f = from ?? clock.UtcNow.AddDays(-1); var e = to ?? f.AddDays(31);
            q = q.Where(b => b.StartUtc >= f && b.StartUtc < e);
            if (includeCancelled != true) q = q.Where(b => b.Status == BookingStatus.Confirmed);
            return Results.Ok((await q.OrderBy(b => b.StartUtc).Take(500).ToListAsync(ct)).Select(BookingDto));
        });

        g.MapPost("/bookings", async (InternalBookIn req, SchedulingDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            var t = await db.Types.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.AppointmentTypeId && x.Enabled, ct);
            if (t is null || !u.HasPermission(Permissions.AppointmentsManage) || !await u.CanAccessProductAsync(t.ProductId, ct)) return Results.NotFound();
            if (ValidateAttendee(req.Name, req.Email, req.Phone, req.Notes) is { } err) return Results.Problem(statusCode: 400, title: err);
            if (req.IdempotencyKey is { Length: > 100 }) return Results.Problem(statusCode: 400, title: "The idempotency key is too long");
            var existing = await ExistingByKey(db, t.Id, req.IdempotencyKey, ct);
            if (existing is not null) return Results.Ok(BookingDto(existing));

            var token = NewToken();
            var template = new Booking { AttendeeName = req.Name.Trim(), AttendeeEmail = req.Email.Trim(), AttendeePhone = req.Phone, Notes = req.Notes, LeadId = req.LeadId, OpportunityId = req.OpportunityId,
                IdempotencyKey = req.IdempotencyKey, ManageTokenHash = Hash(token), CreatedBy = u.UserId };
            var (b, error) = await PlaceAsync(db, t, req.Start.ToUniversalTime(), template, null, clock.UtcNow, ct);
            if (b is null) return Results.Problem(statusCode: 409, title: error);
            await audit.WriteAsync(new AuditEntry("scheduling.booking.created", "Booking", b.Id.ToString(), b.ProductId, b.AttendeeName, After: new { b.StartUtc, b.StaffUserId, b.LeadId }), ct);
            await events.PublishAsync(new PlatformEvent("booking.created", b.ProductId, "booking", b.Id), ct);
            return Results.Created($"/api/v1/scheduling/bookings/{b.Id}", BookingDto(b));
        });

        g.MapPost("/bookings/{id:guid}/cancel", async (Guid id, CancelIn req, SchedulingDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.AppointmentsManage) || !await u.CanAccessProductAsync(b.ProductId, ct)) return Results.NotFound();
            if (b.Status == BookingStatus.Cancelled) return Results.NoContent();
            b.Status = BookingStatus.Cancelled; b.CancelledAt = clock.UtcNow; b.CancelReason = req.Reason?[..Math.Min(req.Reason.Length, 500)];
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("scheduling.booking.cancelled", "Booking", id.ToString(), b.ProductId, b.AttendeeName, After: new { req.Reason, By = "staff" }), ct);
            await events.PublishAsync(new PlatformEvent("booking.cancelled", b.ProductId, "booking", b.Id), ct);
            return Results.NoContent();
        });
    }

    private static Task<Booking?> ExistingByKey(SchedulingDbContext db, Guid typeId, string? key, CancellationToken ct)
        => string.IsNullOrEmpty(key) ? Task.FromResult<Booking?>(null) : db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.AppointmentTypeId == typeId && b.IdempotencyKey == key, ct);

    private static async Task<string?> ValidateType(TypeIn r, IUserDirectory dir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) return "A name is required";
        if (r.DurationMinutes is < 5 or > 480) return "Duration must be between 5 and 480 minutes";
        if (r.BufferBeforeMinutes is < 0 or > 240 || r.BufferAfterMinutes is < 0 or > 240) return "Buffers must be between 0 and 240 minutes";
        if (r.SlotStepMinutes is < 5 or > 240) return "The slot step must be between 5 and 240 minutes";
        if (r.MinNoticeHours is < 0 or > 720) return "Minimum notice must be between 0 and 720 hours";
        if (r.MaxDaysAhead is < 1 or > 365) return "Booking window must be between 1 and 365 days";
        if (Zone(r.TimeZone ?? "") is null) return "Unknown time zone; use an IANA name such as Asia/Karachi";
        if (r.StaffUserIds is null || r.StaffUserIds.Length == 0 || r.StaffUserIds.Length > 50) return "Choose between 1 and 50 staff members";
        foreach (var s in r.StaffUserIds) if (!await dir.IsActiveUserAsync(s, ct)) return "The staff list contains an unknown or inactive user";
        return null;
    }

    private static void Apply(AppointmentType t, TypeIn r)
    {
        t.Name = r.Name.Trim(); t.Description = r.Description; t.DurationMinutes = r.DurationMinutes;
        t.BufferBeforeMinutes = r.BufferBeforeMinutes ?? 0; t.BufferAfterMinutes = r.BufferAfterMinutes ?? 0; t.SlotStepMinutes = r.SlotStepMinutes ?? 30;
        t.MinNoticeHours = r.MinNoticeHours ?? 4; t.MaxDaysAhead = r.MaxDaysAhead ?? 60; t.TimeZone = r.TimeZone;
        t.StaffUserIds = string.Join(',', r.StaffUserIds.Distinct()); if (r.Enabled is { } en) t.Enabled = en;
    }

    // ---------- public booking page ----------
    private static void MapPublic(RouteGroupBuilder g)
    {
        g.MapGet("/{key}", async (string key, SchedulingDbContext db, CancellationToken ct) =>
        {
            var t = await db.Types.AsNoTracking().FirstOrDefaultAsync(x => x.PublicKey == key && x.Enabled, ct);
            return t is null ? Results.NotFound() : Results.Ok(new { t.Name, t.Description, t.DurationMinutes, t.TimeZone });
        });

        g.MapGet("/{key}/slots", async (string key, DateTimeOffset? from, DateTimeOffset? to, SchedulingDbContext db, IClock clock, CancellationToken ct) =>
        {
            var t = await db.Types.AsNoTracking().FirstOrDefaultAsync(x => x.PublicKey == key && x.Enabled, ct);
            if (t is null) return Results.NotFound();
            var now = clock.UtcNow; var f = from ?? now; var e = to ?? f.AddDays(14);
            if (e <= f || e - f > TimeSpan.FromDays(31)) return Results.Problem(statusCode: 400, title: "Ask for up to 31 days");
            var (slots, _) = await SlotsAsync(db, t, now, f, e, ct);
            return Results.Ok(slots.Select(s => new { start = s.Start, end = s.End }));   // staff identities are never exposed
        });

        g.MapPost("/{key}/book", async (string key, PublicBookIn req, HttpContext http, SchedulingDbContext db, IClock clock, IConfiguration cfg, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            if (http.Request.ContentLength > 16 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var t = await db.Types.AsNoTracking().FirstOrDefaultAsync(x => x.PublicKey == key && x.Enabled, ct);
            if (t is null) return Results.NotFound();
            if (ValidateAttendee(req.Name, req.Email, req.Phone, req.Notes) is { } err) return Results.Problem(statusCode: 400, title: err);
            if (req.IdempotencyKey is { Length: > 100 }) return Results.Problem(statusCode: 400, title: "The idempotency key is too long");

            // A retried request must not create a second booking, and must not leak the first one's secret link, so the repeat just confirms.
            var existing = await ExistingByKey(db, t.Id, req.IdempotencyKey, ct);
            if (existing is not null) return Results.Ok(new { bookingId = existing.Id, start = existing.StartUtc, end = existing.EndUtc, repeated = true });

            var leadId = VerifyLeadRef(req.Ref, cfg["Scheduling:LinkSecret"]);   // an invalid or missing ref simply books without a lead
            var token = NewToken();
            var template = new Booking { AttendeeName = req.Name.Trim(), AttendeeEmail = req.Email.Trim(), AttendeePhone = req.Phone, Notes = req.Notes, LeadId = leadId,
                IdempotencyKey = req.IdempotencyKey, ManageTokenHash = Hash(token) };
            var (b, error) = await PlaceAsync(db, t, req.Start.ToUniversalTime(), template, null, clock.UtcNow, ct);
            if (b is null) return Results.Problem(statusCode: 409, title: error);
            await audit.WriteAsync(new AuditEntry("scheduling.booking.created", "Booking", b.Id.ToString(), b.ProductId, b.AttendeeName, After: new { b.StartUtc, Source = "public", b.LeadId }), ct);
            await events.PublishAsync(new PlatformEvent("booking.created", b.ProductId, "booking", b.Id), ct);
            return Results.Created($"/api/v1/public/booking/manage/{token}", new { bookingId = b.Id, start = b.StartUtc, end = b.EndUtc, manageToken = token, manageUrl = $"/booking/{token}" });
        });

        g.MapGet("/manage/{token}", async (string token, SchedulingDbContext db, CancellationToken ct) =>
        {
            var b = await ByToken(db, token, ct);
            if (b is null) return Results.NotFound();
            var t = await db.Types.AsNoTracking().FirstAsync(x => x.Id == b.AppointmentTypeId, ct);
            return Results.Ok(new { appointment = t.Name, start = b.StartUtc, end = b.EndUtc, b.Status, t.TimeZone, publicKey = t.PublicKey, b.AttendeeName });
        });

        g.MapPost("/manage/{token}/cancel", async (string token, CancelIn req, SchedulingDbContext db, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            var b = await ByToken(db, token, ct);
            if (b is null) return Results.NotFound();
            if (b.Status == BookingStatus.Confirmed)
            {
                b.Status = BookingStatus.Cancelled; b.CancelledAt = clock.UtcNow; b.CancelReason = req.Reason?[..Math.Min(req.Reason.Length, 500)];
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(new AuditEntry("scheduling.booking.cancelled", "Booking", b.Id.ToString(), b.ProductId, b.AttendeeName, After: new { req.Reason, By = "attendee" }), ct);
                await events.PublishAsync(new PlatformEvent("booking.cancelled", b.ProductId, "booking", b.Id), ct);
            }
            return Results.NoContent();
        });

        g.MapPost("/manage/{token}/reschedule", async (string token, RescheduleIn req, SchedulingDbContext db, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            var old = await ByToken(db, token, ct);
            if (old is null) return Results.NotFound();
            if (old.Status != BookingStatus.Confirmed) return Results.Problem(statusCode: 409, title: "This booking is cancelled");
            var t = await db.Types.AsNoTracking().FirstAsync(x => x.Id == old.AppointmentTypeId && x.Enabled, ct);
            var newToken = NewToken();
            var template = new Booking { AttendeeName = old.AttendeeName, AttendeeEmail = old.AttendeeEmail, AttendeePhone = old.AttendeePhone, Notes = old.Notes, LeadId = old.LeadId, OpportunityId = old.OpportunityId, ManageTokenHash = Hash(newToken) };
            var (b, error) = await PlaceAsync(db, t, req.Start.ToUniversalTime(), template, old, clock.UtcNow, ct);
            if (b is null) return Results.Problem(statusCode: 409, title: error);
            await audit.WriteAsync(new AuditEntry("scheduling.booking.rescheduled", "Booking", b.Id.ToString(), b.ProductId, b.AttendeeName, Before: new { old.StartUtc }, After: new { b.StartUtc }), ct);
            await events.PublishAsync(new PlatformEvent("booking.cancelled", b.ProductId, "booking", old.Id, new Dictionary<string, string?> { ["reason"] = "rescheduled" }), ct);
            await events.PublishAsync(new PlatformEvent("booking.created", b.ProductId, "booking", b.Id, new Dictionary<string, string?> { ["rescheduled"] = "true" }), ct);
            return Results.Ok(new { bookingId = b.Id, start = b.StartUtc, end = b.EndUtc, manageToken = newToken, manageUrl = $"/booking/{newToken}" });
        });
    }

    private static Task<Booking?> ByToken(SchedulingDbContext db, string token, CancellationToken ct)
        => token.Length is < 20 or > 64 ? Task.FromResult<Booking?>(null) : db.Bookings.FirstOrDefaultAsync(b => b.ManageTokenHash == Hash(token), ct);
}
