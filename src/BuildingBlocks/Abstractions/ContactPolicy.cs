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
