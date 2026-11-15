namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public enum ContactChannel { Email, Phone, WhatsApp, Sms }

/// <summary>Marketing needs explicit, recorded opt-in. Transactional covers relationship messages (booking confirmations, reminders the person asked for).</summary>
public enum ContactPurpose { Marketing, Transactional }

public sealed record ContactDecision(bool Allowed, string Reason, string? Address = null);

/// <summary>
/// THE single choke point for "may we message this person?" (Architecture 7: check consent and opt-out immediately before sending).
/// Every sender (automation, reminders, email/WhatsApp adapters) must call this and must not send when Allowed is false.
/// </summary>
public interface IContactPolicy
{
    Task<ContactDecision> CanContactAsync(Guid contactId, ContactChannel channel, ContactPurpose purpose, CancellationToken ct = default);
}

/// <summary>Address-level checks for people who are not (yet) contacts, such as someone who booked a meeting from a public page.</summary>
public interface IAddressPolicy
{
    /// <summary>
    /// Relationship (Transactional) messages to a bare address are allowed unless the address is suppressed or the messaging pause is on.
    /// Marketing to a bare address is never allowed: marketing needs a contact with a recorded opt-in.
    /// If the address belongs to a known contact, that contact's own rules apply.
    /// </summary>
    Task<ContactDecision> CanContactAddressAsync(ContactChannel channel, string address, ContactPurpose purpose, CancellationToken ct = default);
}

/// <summary>Records an opt-out (unsubscribe link, bounce, complaint) in the CRM so every sender honours it from then on.</summary>
public interface IOptOutService
{
    /// <returns>True if the address was newly suppressed.</returns>
    /// <param name="marketingOnly">True for an unsubscribe click: marketing stops, relationship messages (booking confirmations) continue. False for bounces and complaints: nothing more is sent to the address.</param>
    Task<bool> OptOutAsync(ContactChannel channel, string address, string reason, bool marketingOnly, CancellationToken ct = default);
}
