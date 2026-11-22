namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>A short link that counts clicks and then sends the visitor on to the real address (with UTM tags, and the Google Play referrer for store pages).</summary>
public sealed record TrackedLink(Guid Id, string Code, string ShortUrl, string Destination);

/// <summary>
/// Clicks on a post's links. A click is someone opening the link; it is not an install, a view or a sale, and nothing here claims otherwise.
/// Visitors are counted per day from a one-way hash, so Unique is "about this many different people", never an identity.
/// </summary>
public sealed record LinkClicks(Guid ContentItemId, int Clicks, int Unique, DateTimeOffset? LastClickAt);

/// <summary>Implemented by the Links module.</summary>
public interface ITrackedLinks
{
    /// <summary>Makes a short link for an address. Returns null when no public base address is configured, so the caller keeps the original address.</summary>
    Task<TrackedLink?> CreateAsync(Guid productId, string destinationUrl, string channel, string campaign, string? label, CancellationToken ct = default);

    /// <summary>Makes a new short link for the same destination as an existing short link of this product, for another channel (the Instagram copy of a post). Null when the address is not this product's short link.</summary>
    Task<TrackedLink?> RetargetAsync(Guid productId, string shortUrl, string channel, string? label, CancellationToken ct = default);

    /// <summary>Ties links made while drafting to the post they ended up in, so clicks can be reported per post.</summary>
    Task AttachAsync(IReadOnlyCollection<Guid> linkIds, Guid contentItemId, CancellationToken ct = default);

    /// <summary>Of the given addresses, those that are short links belonging to this product (used by the reviewer's "own links only" check).</summary>
    Task<IReadOnlySet<string>> OwnShortUrlsAsync(Guid productId, IReadOnlyCollection<string> urls, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, LinkClicks>> ClicksByContentAsync(IReadOnlyCollection<Guid> contentItemIds, DateTimeOffset? since = null, CancellationToken ct = default);
}
