using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Scheduling;

/// <summary>What workflows may know about a booking. The host (staff member) is reported as <c>ownerUserId</c>.</summary>
public sealed class BookingStateProvider(SchedulingDbContext db) : ISubjectStateProvider
{
    public string SubjectType => "booking";

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return null;
        return new Dictionary<string, object?>
        {
            ["status"] = b.Status.ToString(), ["attendeeName"] = b.AttendeeName, ["attendeeEmail"] = b.AttendeeEmail, ["start"] = b.StartUtc.ToString("u"),
            ["ownerUserId"] = b.StaffUserId, ["leadId"] = b.LeadId,
        };
    }
}
