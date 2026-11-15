namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public sealed record DraftSummary(Guid Id, Guid ProductId, string Kind, string Title, string Body, int ProfileVersion, string Provider, string Model, DateTimeOffset CreatedAt);

/// <summary>Lets other modules (strategy, content) adopt an AI draft by id without referencing the Ai module.</summary>
public interface IAiDraftLookup
{
    /// <summary>Returns the draft only if it belongs to the product; null otherwise.</summary>
    Task<DraftSummary?> GetAsync(Guid draftId, Guid productId, CancellationToken ct = default);
}
