using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

// What the growth-plan module needs from other modules. It depends on none of them: each implements one of these.

public sealed record DraftedContent(Guid DraftId, string Title, string Body, IReadOnlyList<string> Questions);

/// <summary>Writes one piece of content from the approved product profile and saves it as an AI draft (implemented by the AI module).</summary>
public interface IContentDrafter
{
    /// <summary>Fails (not throws) when the product has no approved profile or the language is not a verified market language; throws <see cref="AiException"/> for provider, budget or pause problems.</summary>
    Task<Result<DraftedContent>> DraftPostAsync(Guid productId, string brief, string? channel, string? language, CancellationToken ct = default);
}

public sealed record NewContent(Guid ProductId, string Title, string Channel, string Format, string? Language, string Body, Guid? SourceDraftId, IReadOnlyList<Guid>? AssetIds = null);

public sealed record ContentStatusInfo(Guid ItemId, string Title, string Status, int CurrentVersion, int? ApprovedVersion, DateTimeOffset? ScheduledFor);

/// <summary>Creates, submits and schedules content on behalf of the caller (implemented by the content module, which keeps every rule: versions, approval of the exact version, no scheduling unapproved text).</summary>
public interface IContentPlanner
{
    Task<Guid> CreateAsync(NewContent content, CancellationToken ct = default);
    /// <summary>Draft -> InReview, and asks for approval of the current version.</summary>
    Task<StateChange> SubmitAsync(Guid itemId, CancellationToken ct = default);
    /// <summary>Approved -> Scheduled at the given time. Refused unless the current version is approved and the time is in the future.</summary>
    Task<StateChange> ScheduleAsync(Guid itemId, DateTimeOffset at, CancellationToken ct = default);
    Task<IReadOnlyList<ContentStatusInfo>> GetStatusesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}

/// <summary>The network's latest figures for one published post. A null figure was not supplied by the network and must never be shown or treated as zero.</summary>
public sealed record PostPerformance(Guid ContentItemId, DateTimeOffset PublishedAt, string? Url, DateTimeOffset? MeasuredAt, int? Reactions, int? Comments, int? Shares, long? Views, long? Reach, int? Clicks, string? Note);

/// <summary>Implemented by the publishing module.</summary>
public interface IPostPerformanceReader
{
    Task<IReadOnlyList<PostPerformance>> GetAsync(IReadOnlyCollection<Guid> contentItemIds, CancellationToken ct = default);
}
