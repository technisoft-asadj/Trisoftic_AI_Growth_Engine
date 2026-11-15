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

/// <summary>Consent rules for addresses that may not belong to a contact yet.</summary>
public sealed class AddressPolicy(CrmDbContext db, IPauseSwitch pause, IContactPolicy contacts) : IAddressPolicy
{
    public async Task<ContactDecision> CanContactAddressAsync(ContactChannel channel, string address, ContactPurpose purpose, CancellationToken ct = default)
    {
        if (await pause.IsPausedAsync(PauseScope.Messaging, ct)) return new(false, "Messaging is paused by an administrator");
        var norm = Normalizer.Method(Normalizer.MethodFor(channel), address);
        if (norm is null) return new(false, "Not a valid address");

        // A known person is judged by their own consent record.
        var type = Normalizer.MethodFor(channel);
        var contactId = await db.Methods.AsNoTracking().Where(m => m.Type == type && m.NormalizedValue == norm).Select(m => (Guid?)m.ContactId).FirstOrDefaultAsync(ct);
        if (contactId is { } cid)
        {
            var merged = await db.Contacts.AsNoTracking().Where(c => c.Id == cid).Select(c => c.MergedIntoId).FirstOrDefaultAsync(ct);
            return await contacts.CanContactAsync(merged ?? cid, channel, purpose, ct);
        }

        if (purpose == ContactPurpose.Marketing) return new(false, "No marketing consent recorded (this address is not a contact)");
        var blocked = await db.Suppressions.AsNoTracking().FirstOrDefaultAsync(s => s.Channel == channel && s.NormalizedValue == norm, ct);
        if (blocked is not null) return new(false, $"Address is suppressed ({blocked.Reason})");
        return new(true, "Relationship message allowed", address.Trim());
    }
}

public sealed class OptOutService(CrmDbContext db, IClock clock, IAuditWriter audit) : IOptOutService
{
    public async Task<bool> OptOutAsync(ContactChannel channel, string address, string reason, bool marketingOnly, CancellationToken ct = default)
    {
        var type = Normalizer.MethodFor(channel);
        var norm = Normalizer.Method(type, address);
        if (norm is null) return false;
        var now = clock.UtcNow;
        var changed = false;

        var contactId = await db.Methods.AsNoTracking().Where(m => m.Type == type && m.NormalizedValue == norm).Select(m => (Guid?)m.ContactId).FirstOrDefaultAsync(ct);
        if (contactId is { } cid)
        {
            var latest = await db.Consents.AsNoTracking().Where(c => c.ContactId == cid && c.Channel == channel && c.Purpose == ContactPurpose.Marketing)
                .OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.Id).Select(c => (ConsentStatus?)c.Status).FirstOrDefaultAsync(ct);
            if (latest != ConsentStatus.Withdrawn)
            {
                db.Consents.Add(new ConsentRecord { ContactId = cid, Channel = channel, Purpose = ContactPurpose.Marketing, Status = ConsentStatus.Withdrawn, Source = reason, Evidence = "Recorded automatically from the person's own action", RecordedAt = now });
                db.Interactions.Add(new Interaction { ContactId = cid, Type = InteractionType.System, Summary = $"Marketing consent withdrawn: {reason}", OccurredAt = now });
                changed = true;
            }
        }
        if (!marketingOnly && !await db.Suppressions.AnyAsync(s => s.Channel == channel && s.NormalizedValue == norm, ct))
        {
            db.Suppressions.Add(new Suppression { Channel = channel, NormalizedValue = norm, Reason = reason, CreatedAt = now });
            changed = true;
        }
        if (!changed) return false;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }   // a parallel request got there first; the outcome is the same
        await audit.WriteAsync(new AuditEntry(marketingOnly ? "crm.unsubscribed" : "crm.suppression.added", "Contact", contactId?.ToString() ?? norm, Summary: $"{channel}: {reason}"), ct);
        return true;
    }
}
