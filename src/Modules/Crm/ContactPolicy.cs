using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Crm;

/// <summary>
/// Consent/eligibility decision, evaluated at send time (never cached): messaging pause, contact exists and is not merged,
/// an address exists, the address is not suppressed, and for Marketing the latest consent record is a recorded Granted.
/// </summary>
public sealed class ContactPolicy(CrmDbContext db, IPauseSwitch pause) : IContactPolicy
{
    public async Task<ContactDecision> CanContactAsync(Guid contactId, ContactChannel channel, ContactPurpose purpose, CancellationToken ct = default)
    {
        if (await pause.IsPausedAsync(PauseScope.Messaging, ct)) return new(false, "Messaging is paused by an administrator");

        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null) return new(false, "Contact not found");
        if (contact.MergedIntoId is not null) return new(false, "Contact was merged into another record");

        var types = Normalizer.MethodsFor(channel).ToArray();
        var methods = await db.Methods.AsNoTracking().Where(m => m.ContactId == contactId && types.Contains(m.Type)).OrderByDescending(m => m.IsPrimary).ToListAsync(ct);
        if (methods.Count == 0) return new(false, $"No {channel} address on file");

        var values = methods.Select(m => m.NormalizedValue).ToList();
        var blocked = await db.Suppressions.AsNoTracking().FirstOrDefaultAsync(s => s.Channel == channel && values.Contains(s.NormalizedValue), ct);
        if (blocked is not null) return new(false, $"Address is suppressed ({blocked.Reason})");

        var latest = await db.Consents.AsNoTracking().Where(c => c.ContactId == contactId && c.Channel == channel && c.Purpose == purpose)
            .OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.Id).FirstOrDefaultAsync(ct);

        if (purpose == ContactPurpose.Marketing)
        {
            if (latest is null) return new(false, "No marketing consent recorded");
            if (latest.Status == ConsentStatus.Withdrawn) return new(false, "Marketing consent was withdrawn");
        }
        else if (latest is { Status: ConsentStatus.Withdrawn })
        {
            return new(false, "The contact opted out of this channel");
        }
        return new(true, purpose == ContactPurpose.Marketing ? "Marketing consent on record" : "Relationship message allowed", methods[0].Value);
    }
}
