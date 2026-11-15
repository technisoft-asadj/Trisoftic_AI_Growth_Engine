namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>
/// An email to send. Give a ContactId (consent and opt-outs apply) or, for relationship messages only, a bare address.
/// The consent check is made again at the moment of sending, not just here.
/// </summary>
public sealed record EmailRequest(
    ContactPurpose Purpose, string Subject, string Body, string DedupKey,
    Guid? ContactId = null, string? ToAddress = null, Guid? ProductId = null, string? SubjectType = null, Guid? SubjectId = null);

public sealed record QueuedEmail(Guid MessageId, bool AlreadyQueued);

/// <summary>Queues email for sending. Implemented by the Messaging module; the only way other modules send email.</summary>
public interface IMessageOutbox
{
    /// <summary>Idempotent on <see cref="EmailRequest.DedupKey"/>: asking twice queues one message.</summary>
    Task<QueuedEmail> QueueEmailAsync(EmailRequest request, CancellationToken ct = default);
}
