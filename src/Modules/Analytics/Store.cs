using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Analytics;

public static class BuiltInMetrics
{
    private static MetricDefinition M(string key, string name, string cat, MetricUnit unit, Aggregation agg, Direction dir, string def)
        => new() { Key = key, Name = name, Category = cat, Unit = unit, Aggregation = agg, Direction = dir, Definition = def, IsBuiltIn = true };

    public static readonly IReadOnlyList<MetricDefinition> All =
    [
        M("impressions", "Impressions", "Reach", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "Times content was shown on screen, counting repeat views by the same person."),
        M("reach", "Reach", "Reach", MetricUnit.Count, Aggregation.None, Direction.HigherIsBetter, "Unique accounts that saw content during the period. Unique counts cannot be added across periods, so each period is shown on its own."),
        M("video_views", "Video views", "Reach", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "Times a video was played, using the network's own definition of a view."),
        M("followers", "Followers", "Reach", MetricUnit.Count, Aggregation.Latest, Direction.HigherIsBetter, "Total followers at the end of the period (a level, not a flow)."),
        M("engagements", "Engagements", "Engagement", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "Reactions, comments, shares and other interactions, as the network counts them."),
        M("clicks", "Link clicks", "Engagement", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "Clicks on links to the product's own destination."),
        M("ctr", "Click-through rate", "Engagement", MetricUnit.Percent, Aggregation.None, Direction.HigherIsBetter, "Clicks divided by impressions, as reported by the source. Not recomputed here."),
        M("sessions", "Website sessions", "Acquisition", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "Visits to the product's website, as reported by the web analytics source."),
        M("installs", "Installs", "Acquisition", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "App installs reported by the store or attribution source. Attribution to a campaign depends on that source."),
        M("signups", "Sign-ups", "Acquisition", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "New accounts created in the product."),
        M("leads", "Leads reported", "Acquisition", MetricUnit.Count, Aggregation.Sum, Direction.HigherIsBetter, "Leads reported by an ad or web source. The CRM dashboard counts leads actually captured in the platform."),
        M("conversion_rate", "Conversion rate", "Acquisition", MetricUnit.Percent, Aggregation.None, Direction.HigherIsBetter, "Share of visitors or installs that completed the target action, as reported by the source."),
        M("retention_d1", "Day-1 retention", "Product", MetricUnit.Percent, Aggregation.None, Direction.HigherIsBetter, "Share of new users who came back the next day."),
        M("retention_d7", "Day-7 retention", "Product", MetricUnit.Percent, Aggregation.None, Direction.HigherIsBetter, "Share of new users who came back seven days later."),
        M("spend", "Spend", "Money", MetricUnit.Money, Aggregation.Sum, Direction.Neutral, "Money actually spent on marketing, per currency. Different currencies are never added together."),
        M("revenue", "Revenue", "Money", MetricUnit.Money, Aggregation.Sum, Direction.HigherIsBetter, "Revenue recorded for the period, per currency."),
    ];

    public static async Task SeedAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var have = (await db.Definitions.Select(d => d.Key).ToListAsync(ct)).ToHashSet();
        foreach (var d in All.Where(d => !have.Contains(d.Key))) db.Definitions.Add(d);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Both directions of the metric contracts: other modules write measurements in (pulls) and read them out (the planner).</summary>
public sealed class MetricStore(AnalyticsDbContext db) : IMetricSink, IMetricReader
{
    public async Task<int> RecordAsync(string sourceName, string sourceKind, Guid productId, IReadOnlyList<MetricPoint> points, DateTimeOffset retrievedAt, CancellationToken ct = default)
    {
        var kind = Enum.TryParse<SourceKind>(sourceKind, true, out var k) ? k : SourceKind.Platform;
        var source = await db.Sources.FirstOrDefaultAsync(s => s.ProductId == productId && s.Name == sourceName, ct);
        if (source is null)
        {
            source = new DataSource { ProductId = productId, Name = sourceName, Kind = kind, FreshnessHours = kind == SourceKind.Manual ? 24 * 35 : 48, CreatedAt = retrievedAt };
            db.Sources.Add(source);
        }
        var defs = await db.Definitions.AsNoTracking().ToDictionaryAsync(d => d.Key, ct);
        var stored = 0;
        foreach (var p in points)
        {
            if (!defs.TryGetValue(p.MetricKey, out var def) || p.PeriodEnd < p.PeriodStart) continue;
            var currency = def.Unit == MetricUnit.Money ? (p.Currency ?? "").ToUpperInvariant() : "";
            if (def.Unit == MetricUnit.Money && currency.Length != 3) continue;
            if (p.Value is < 0) continue;
            var channel = p.Channel ?? ""; var market = p.Market ?? ""; var campaign = p.CampaignId ?? Guid.Empty;
            var existing = await db.Observations.FirstOrDefaultAsync(o => o.ProductId == productId && o.DataSourceId == source.Id && o.MetricKey == p.MetricKey && o.PeriodStart == p.PeriodStart
                && o.PeriodEnd == p.PeriodEnd && o.Channel == channel && o.Market == market && o.CampaignId == campaign && o.Currency == currency, ct);
            if (existing is null)
                db.Observations.Add(new MetricObservation { ProductId = productId, DataSourceId = source.Id, MetricKey = p.MetricKey, PeriodStart = p.PeriodStart, PeriodEnd = p.PeriodEnd, Value = p.Value,
                    Status = p.Value is null ? ObservationStatus.Unavailable : ObservationStatus.Measured, Currency = currency, Channel = channel, Market = market, CampaignId = campaign, RetrievedAt = retrievedAt });
            else { existing.Value = p.Value; existing.Status = p.Value is null ? ObservationStatus.Unavailable : ObservationStatus.Measured; existing.RetrievedAt = retrievedAt; }
            stored++;
        }
        source.LastRetrievedAt = retrievedAt;   // even a pull that returned nothing proves the source was reachable
        await db.SaveChangesAsync(ct);
        return stored;
    }

    public async Task<IReadOnlyList<MetricPoint>> QueryAsync(Guid productId, IReadOnlyCollection<string> metricKeys, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.Observations.AsNoTracking().Where(o => o.ProductId == productId && metricKeys.Contains(o.MetricKey) && o.PeriodStart >= from && o.PeriodEnd <= to).ToListAsync(ct);
        return rows.GroupBy(o => (o.MetricKey, o.PeriodStart, o.PeriodEnd, o.Channel.ToLowerInvariant(), o.Market.ToLowerInvariant(), o.CampaignId, o.Currency))
            .Select(g => g.OrderByDescending(o => o.RetrievedAt).First())
            .Select(o => new MetricPoint(o.ProductId, o.MetricKey, o.PeriodStart, o.PeriodEnd, o.Value, o.Channel, o.Market, o.CampaignId == Guid.Empty ? null : o.CampaignId, o.Currency == "" ? null : o.Currency)).ToList();
    }
}

/// <summary>What workflows may know about a data source when it goes stale.</summary>
public sealed class DataSourceStateProvider(AnalyticsDbContext db, IClock clock) : ISubjectStateProvider
{
    public string SubjectType => "datasource";

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;
        var hours = (clock.UtcNow - (s.LastRetrievedAt ?? s.CreatedAt)).TotalHours;
        return new Dictionary<string, object?> { ["name"] = s.Name, ["kind"] = s.Kind.ToString(), ["hoursSinceUpdate"] = Math.Round(hours, 1), ["freshnessHours"] = s.FreshnessHours };
    }
}

/// <summary>Raises "analytics.source-stale" once a day for each source that has gone quiet for longer than its freshness window.</summary>
public sealed class FreshnessCheckHandler(AnalyticsDbContext db, IEventPublisher events, IClock clock) : IJobHandler
{
    public const string CheckJob = "analytics.freshness-check";
    public string Type => CheckJob;
    public PauseScope? PauseScope => null;

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = (await db.Sources.Where(s => s.Enabled && s.ProductId != null).ToListAsync(ct))
            .Where(s => Freshness.IsStale(s.LastRetrievedAt ?? s.CreatedAt, s.FreshnessHours, now) && (s.LastStaleAlertAt is null || now - s.LastStaleAlertAt > TimeSpan.FromHours(24))).ToList();
        foreach (var s in due)
        {
            s.LastStaleAlertAt = now;
            await db.SaveChangesAsync(ct);   // claim first: a duplicate run must not alert twice
            await events.PublishAsync(new PlatformEvent("analytics.source-stale", s.ProductId, "datasource", s.Id), ct);
        }
    }
}

public sealed class AnalyticsScheduler(IServiceScopeFactory scopes, IClock clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest(FreshnessCheckHandler.CheckJob, "{}", IdempotencyKey: $"fresh:{clock.UtcNow.ToUnixTimeSeconds() / 3600}"), stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { /* next tick */ }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
