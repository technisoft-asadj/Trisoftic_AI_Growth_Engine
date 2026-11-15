namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>
/// Something that happened in a module that other modules may react to (a lead was created, a deal moved stage, a booking was made).
/// Events carry identity only; consumers read current state through <see cref="ISubjectStateProvider"/>, so they never act on stale copies.
/// </summary>
/// <param name="Type">Dotted name, e.g. "lead.created".</param>
/// <param name="Data">Small string facts known at publish time (e.g. the previous status). Optional.</param>
public sealed record PlatformEvent(string Type, Guid? ProductId, string SubjectType, Guid SubjectId, IReadOnlyDictionary<string, string?>? Data = null);

public interface IEventPublisher
{
    /// <summary>Hands the event to subscribers. Best effort: a failure here must not undo the business change that already committed.</summary>
    Task PublishAsync(PlatformEvent e, CancellationToken ct = default);
}

/// <summary>Lets a module expose the current facts about one of its records to other modules without exposing its database.</summary>
public interface ISubjectStateProvider
{
    /// <summary>"lead", "opportunity", "booking"...</summary>
    string SubjectType { get; }

    /// <summary>Flat facts (strings, numbers, bools, null) about the record, or null if it no longer exists. By convention <c>ownerUserId</c> is the responsible person.</summary>
    Task<IReadOnlyDictionary<string, object?>?> GetAsync(Guid id, CancellationToken ct);
}
