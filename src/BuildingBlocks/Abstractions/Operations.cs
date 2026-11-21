namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>Whether a connected account can still post. Status is Connected, NeedsReconnect or Disconnected. Never carries a token.</summary>
public sealed record ChannelHealth(string Channel, string Name, string Status, DateTimeOffset? TokenExpiresAt, string? LastError);

/// <summary>Implemented by Publishing. Lets the autopilot warn about an account that is about to stop working.</summary>
public interface IChannelHealth
{
    Task<IReadOnlyList<ChannelHealth>> ListAsync(Guid productId, CancellationToken ct);
}

/// <summary>This month's AI spend against the limits. Implemented by the Ai module.</summary>
public sealed record AiSpend(decimal ProductUsd, decimal ProductBudgetUsd, decimal GlobalUsd, decimal GlobalBudgetUsd, decimal AlertThreshold);

public interface IAiSpendReader
{
    Task<AiSpend> GetAsync(Guid productId, CancellationToken ct);
}
