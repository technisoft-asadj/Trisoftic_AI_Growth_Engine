namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>
/// One measured (or honestly unmeasurable) number. A null Value means "the source could not provide it"; it is never treated as zero.
/// Channel and Market are empty strings (not null) when the number applies to all of them.
/// </summary>
public sealed record MetricPoint(Guid ProductId, string MetricKey, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Value, string Channel = "", string Market = "", Guid? CampaignId = null, string? Currency = null);

/// <summary>Where other modules (a platform pull, a connector) hand measurements to analytics.</summary>
public interface IMetricSink
{
    /// <summary>
    /// Stores the points under a named data source (created on first use) and marks the source as freshly retrieved.
    /// A point replaces an earlier one for the same source, metric, period, channel, market and campaign. Returns how many points were stored.
    /// </summary>
    Task<int> RecordAsync(string sourceName, string sourceKind, Guid productId, IReadOnlyList<MetricPoint> points, DateTimeOffset retrievedAt, CancellationToken ct = default);
}

/// <summary>Read access to measurements for modules that reason about them (the budget planner).</summary>
public interface IMetricReader
{
    /// <summary>Observations in the period. Where several sources report the same figure, the most recently retrieved one is returned, once.</summary>
    Task<IReadOnlyList<MetricPoint>> QueryAsync(Guid productId, IReadOnlyCollection<string> metricKeys, DateOnly from, DateOnly to, CancellationToken ct = default);
}
