using System.Globalization;
using System.Text.RegularExpressions;

namespace TriSoftic.GrowthEngine.Modules.Analytics;

public sealed record ParsedRow(int Row, string MetricKey, DateOnly Start, DateOnly End, decimal? Value, string Channel, string Market, Guid CampaignId, string Currency);
public sealed record RowError(int Row, string Message);

/// <summary>Turns an uploaded sheet into validated observations. Every row either becomes an observation or an error with its row number; none is dropped silently.</summary>
public static partial class ImportParser
{
    public const int MaxRows = 5000;

    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyRx();

    private static string Norm(string s) => s.Trim().ToLowerInvariant().Replace("_", "").Replace(" ", "");

    public static (List<ParsedRow> Rows, List<RowError> Errors, string? FileProblem) Parse(IReadOnlyList<string[]> sheet, IReadOnlyDictionary<string, MetricDefinition> defs, DateOnly today)
    {
        var rows = new List<ParsedRow>(); var errors = new List<RowError>();
        if (sheet.Count < 2) return (rows, errors, "The file needs a header row and at least one data row");
        if (sheet.Count - 1 > MaxRows) return (rows, errors, $"At most {MaxRows} rows per import");

        var col = new Dictionary<string, int>();
        for (var i = 0; i < sheet[0].Length; i++) col.TryAdd(Norm(sheet[0][i]), i);
        if (!col.ContainsKey("metric") || !(col.ContainsKey("periodstart") || col.ContainsKey("date")) || !col.ContainsKey("value"))
            return (rows, errors, "The header needs the columns: metric, periodStart (or date), value. Optional: periodEnd, channel, market, campaignId, currency");
        string? Cell(string[] r, params string[] names) { foreach (var n in names) if (col.TryGetValue(n, out var i) && i < r.Length && !string.IsNullOrWhiteSpace(r[i])) return r[i].Trim(); return null; }

        var seen = new HashSet<string>();
        for (var n = 1; n < sheet.Count; n++)
        {
            var r = sheet[n]; var line = n + 1;
            var key = Cell(r, "metric")?.ToLowerInvariant();
            if (key is null) { errors.Add(new(line, "Metric is empty")); continue; }
            if (!defs.TryGetValue(key, out var def)) { errors.Add(new(line, $"Unknown metric '{key}'. Add its definition first, or check the spelling")); continue; }

            if (!TryDate(Cell(r, "periodstart", "date"), out var start)) { errors.Add(new(line, "periodStart is missing or not a date (use YYYY-MM-DD)")); continue; }
            var endText = Cell(r, "periodend");
            var end = start;
            if (endText is not null && !TryDate(endText, out end)) { errors.Add(new(line, "periodEnd is not a date (use YYYY-MM-DD)")); continue; }
            if (end < start) { errors.Add(new(line, "periodEnd is before periodStart")); continue; }
            if (end.DayNumber - start.DayNumber > 366) { errors.Add(new(line, "A period can be at most 366 days")); continue; }
            if (end > today.AddDays(1)) { errors.Add(new(line, "The period ends in the future; data cannot exist yet")); continue; }

            decimal? value = null;
            var raw = Cell(r, "value");
            if (raw is not null && !IsUnavailable(raw))
            {
                if (!decimal.TryParse(raw.Replace(",", ""), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v)) { errors.Add(new(line, $"'{raw}' is not a number (leave it blank or write n/a if the source could not measure it)")); continue; }
                if (v < 0) { errors.Add(new(line, "Negative values are not accepted")); continue; }
                if (def.Unit == MetricUnit.Percent && v > 100) { errors.Add(new(line, "A percentage cannot be above 100")); continue; }
                value = v;
            }

            var currency = (Cell(r, "currency") ?? "").ToUpperInvariant();
            if (def.Unit == MetricUnit.Money) { if (!CurrencyRx().IsMatch(currency)) { errors.Add(new(line, $"'{key}' is money: add a 3-letter currency such as USD or PKR")); continue; } }
            else if (currency != "") { errors.Add(new(line, $"'{key}' is not money, so currency must be blank")); continue; }

            Guid campaign = Guid.Empty;
            if (Cell(r, "campaignid") is { } cid && !Guid.TryParse(cid, out campaign)) { errors.Add(new(line, "campaignId is not a valid id")); continue; }
            var channel = Cell(r, "channel") ?? ""; var market = Cell(r, "market") ?? "";
            if (channel.Length > 60 || market.Length > 60) { errors.Add(new(line, "channel and market are at most 60 characters")); continue; }

            var natural = $"{key}|{start}|{end}|{channel.ToLowerInvariant()}|{market.ToLowerInvariant()}|{campaign}|{currency}";
            if (!seen.Add(natural)) { errors.Add(new(line, "Duplicate of an earlier row in this file (same metric, period, channel, market, campaign, currency)")); continue; }
            rows.Add(new ParsedRow(line, key, start, end, value, channel, market, campaign, currency));
        }
        return (rows, errors, null);
    }

    private static bool IsUnavailable(string s) => s.ToLowerInvariant() is "n/a" or "na" or "-" or "null" or "unavailable";
    private static bool TryDate(string? s, out DateOnly d) { d = default; return s is not null && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d); }
}

public sealed record Obs(Guid SourceId, string SourceName, DateOnly Start, DateOnly End, decimal? Value, string Currency, string Channel, string Market, Guid CampaignId, DateTimeOffset RetrievedAt, bool SourceStale);
public sealed record CurrencyTotal(string Currency, decimal Value);
public sealed record Tile(string Key, string Name, string Category, MetricUnit Unit, string Definition, Direction Direction, Aggregation Aggregation,
    string Status, decimal? Value, IReadOnlyList<CurrencyTotal> ValuesByCurrency, decimal? Latest, DateOnly? LatestPeriodEnd,
    int Periods, int UnavailablePeriods, int Conflicts, IReadOnlyList<string> Sources, DateTimeOffset? OldestRetrieval, bool Stale);

public static class TileBuilder
{
    /// <summary>
    /// Collapses observations into one dashboard figure. Rules: nothing observed is "NoData" and has no value (never 0);
    /// a source that could not measure is "Unavailable", not zero; money is reported per currency and never added across currencies;
    /// when several sources report the same figure the most recently retrieved wins and disagreements are counted.
    /// </summary>
    public static Tile Build(MetricDefinition def, IReadOnlyList<Obs> observations)
    {
        // One figure per (period, channel, market, campaign, currency).
        var chosen = new List<Obs>(); var conflicts = 0;
        foreach (var g in observations.GroupBy(o => (o.Start, o.End, o.Channel.ToLowerInvariant(), o.Market.ToLowerInvariant(), o.CampaignId, o.Currency)))
        {
            var best = g.OrderByDescending(o => o.RetrievedAt).First();
            if (g.Select(o => o.SourceId).Distinct().Count() > 1 && g.Select(o => o.Value).Distinct().Count() > 1) conflicts++;
            chosen.Add(best);
        }

        var measured = chosen.Where(o => o.Value is not null).ToList();
        var status = chosen.Count == 0 ? "NoData" : measured.Count == 0 ? "Unavailable" : measured.Count < chosen.Count ? "Partial" : "Measured";
        var latest = measured.OrderByDescending(o => o.End).ThenByDescending(o => o.RetrievedAt).FirstOrDefault();

        decimal? value = null; IReadOnlyList<CurrencyTotal> byCurrency = [];
        if (measured.Count > 0)
        {
            switch (def.Aggregation)
            {
                case Aggregation.Sum when def.Unit == MetricUnit.Money:
                    byCurrency = measured.GroupBy(o => o.Currency).Select(g => new CurrencyTotal(g.Key, g.Sum(o => o.Value!.Value))).OrderBy(c => c.Currency).ToList(); break;
                case Aggregation.Sum: value = measured.Sum(o => o.Value!.Value); break;
                case Aggregation.Latest when def.Unit == MetricUnit.Money:
                    byCurrency = measured.GroupBy(o => o.Currency).Select(g => g.OrderByDescending(o => o.End).First()).Select(o => new CurrencyTotal(o.Currency, o.Value!.Value)).OrderBy(c => c.Currency).ToList(); break;
                case Aggregation.Latest: value = latest!.Value; break;
            }
        }

        var used = chosen.Select(o => o.SourceName).Distinct().OrderBy(s => s).ToList();
        return new Tile(def.Key, def.Name, def.Category, def.Unit, def.Definition, def.Direction, def.Aggregation, status, value, byCurrency, latest?.Value, latest?.End,
            chosen.Count, chosen.Count - measured.Count, conflicts, used, chosen.Count == 0 ? null : chosen.Min(o => o.RetrievedAt), chosen.Any(o => o.SourceStale));
    }
}

public static class Freshness
{
    public static bool IsStale(DateTimeOffset? lastRetrievedAt, int thresholdHours, DateTimeOffset now) => lastRetrievedAt is null || now - lastRetrievedAt.Value > TimeSpan.FromHours(thresholdHours);
}
