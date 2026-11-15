namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public sealed record DueContent(Guid ItemId, Guid ProductId, string Channel, int Version, DateTimeOffset ScheduledFor);

/// <summary>The exact approved text of one content version, with the facts needed to decide whether it may go out.</summary>
public sealed record PublishableContent(Guid ItemId, Guid ProductId, string Channel, string Format, string Title, string Body, int Version, int CurrentVersion, int? ApprovedVersion, string Status);

public sealed record StateChange(bool Ok, string? Error = null);

public sealed record FailedContent(Guid ItemId, Guid ProductId, string Channel, string Title, int Version, string? Error, DateTimeOffset UpdatedAt);

/// <summary>
/// What the publishing pipeline needs from the content module, and the only way it may change content status.
/// "Published" is reachable only through <see cref="CompletePublishAsync"/>, which the pipeline calls after the provider confirmed the post.
/// </summary>
public interface IContentPublishSource
{
    Task<IReadOnlyList<DueContent>> GetDueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task<PublishableContent?> GetAsync(Guid itemId, int version, CancellationToken ct);

    /// <summary>Scheduled (or already Publishing, on a retry) -> Publishing. Fails if the version is no longer the approved, current one.</summary>
    Task<StateChange> BeginPublishAsync(Guid itemId, int version, CancellationToken ct);
    Task<StateChange> CompletePublishAsync(Guid itemId, int version, DateTimeOffset at, CancellationToken ct);
    Task<StateChange> FailPublishAsync(Guid itemId, int version, string reason, CancellationToken ct);
    /// <summary>PublishFailed -> Scheduled at the given time, so the dispatcher picks it up again.</summary>
    Task<StateChange> RequeueAsync(Guid itemId, int version, DateTimeOffset at, CancellationToken ct);
    /// <summary>PublishFailed -> Approved (give up on this attempt to publish; the approval stays valid).</summary>
    Task<StateChange> AbandonAsync(Guid itemId, int version, CancellationToken ct);
    Task<IReadOnlyList<FailedContent>> GetFailedAsync(IReadOnlyCollection<Guid>? productScope, CancellationToken ct);
}
