using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Planning;

/// <summary>One published post with the figures the network reported. A null figure was not reported and is never counted as zero.</summary>
public sealed record LeaderboardRow(Guid ItemId, string Format, string? HookType, DateTimeOffset PublishedAt, int? Engagements, long? Views, string Visual = "Text only");

/// <summary>
/// One line of the leaderboard. Averages cover only the posts that have the figure. With fewer than <see cref="Leaderboard.MinMeasured"/> measured posts the cell is labelled
/// "too few": it is shown, but it is not evidence and is never ranked above a cell that has enough.
/// </summary>
public sealed record LeaderboardCell(string Key, int Posts, int Measured, decimal? AvgEngagements, int WithViews, decimal? AvgViews, bool TooFew);

public sealed record LeaderboardDimension(string Name, IReadOnlyList<LeaderboardCell> Cells);

public sealed record LeaderboardResult(string Channel, string TimeZoneLabel, int MinMeasured, int Published, int Measured, IReadOnlyList<LeaderboardDimension> Dimensions)
{
    public bool IsEmpty => Published == 0;
}

public static class Leaderboard
{
    public const int MinMeasured = 5;
    public const string Untagged = "(no hook type)";

    public static string SlotOf(DateTimeOffset local) => local.Hour switch
    {
        >= 5 and < 11 => "Morning 05-11",
        >= 11 and < 15 => "Midday 11-15",
        >= 15 and < 19 => "Afternoon 15-19",
        >= 19 and < 23 => "Evening 19-23",
        _ => "Night 23-05",
    };

    public static string ZoneLabel(int offsetHours) => offsetHours == 0 ? "UTC" : $"UTC{(offsetHours > 0 ? "+" : "")}{offsetHours}";

    /// <summary>Engagements = reactions + comments + shares, only when all three are known.</summary>
    public static int? Engagements(PostPerformance? p) => p is null || p.Reactions is null || p.Comments is null || p.Shares is null ? null : p.Reactions + p.Comments + p.Shares;

    public static LeaderboardResult Build(string channel, int offsetHours, IReadOnlyList<LeaderboardRow> rows)
    {
        var off = TimeSpan.FromHours(offsetHours);
        LeaderboardDimension Dim(string name, Func<LeaderboardRow, string> key, IComparer<string>? order = null)
        {
            var cells = rows.GroupBy(key).Select(g =>
            {
                var eng = g.Where(r => r.Engagements is not null).Select(r => (decimal)r.Engagements!.Value).ToList();
                var vw = g.Where(r => r.Views is not null).Select(r => (decimal)r.Views!.Value).ToList();
                return new LeaderboardCell(g.Key, g.Count(), eng.Count, eng.Count == 0 ? null : Math.Round(eng.Average(), 1), vw.Count, vw.Count == 0 ? null : Math.Round(vw.Average(), 1), eng.Count < MinMeasured);
            });
            // Cells with enough evidence first (best average first), then the thin ones: a thin cell never outranks a solid one.
            var sorted = order is null
                ? cells.OrderBy(c => c.TooFew).ThenByDescending(c => c.AvgEngagements ?? -1).ThenBy(c => c.Key, StringComparer.Ordinal)
                : cells.OrderBy(c => c.Key, order);
            return new LeaderboardDimension(name, sorted.ToList());
        }
        var days = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        var dayOrder = Comparer<string>.Create((a, b) => Array.IndexOf(days, a).CompareTo(Array.IndexOf(days, b)));
        var slotOrder = new[] { "Morning 05-11", "Midday 11-15", "Afternoon 15-19", "Evening 19-23", "Night 23-05" };
        var slotCmp = Comparer<string>.Create((a, b) => Array.IndexOf(slotOrder, a).CompareTo(Array.IndexOf(slotOrder, b)));
        return new LeaderboardResult(channel, ZoneLabel(offsetHours), MinMeasured, rows.Count, rows.Count(r => r.Engagements is not null),
        [
            Dim("Format", r => r.Format),
            Dim("Visual", r => r.Visual),
            Dim("Hook type", r => r.HookType ?? Untagged),
            Dim("Weekday", r => (r.PublishedAt + off).DayOfWeek.ToString(), dayOrder),
            Dim("Time slot", r => SlotOf(r.PublishedAt + off), slotCmp),
        ]);
    }

    private static string N(decimal? v) => v is null ? "not reported" : v.Value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The leaderboard as text for the planner. Only these figures may be quoted.</summary>
    public static string ForPrompt(LeaderboardResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LEADERBOARD (posts already published on {r.Channel}; times in {r.TimeZoneLabel}; engagements = reactions + comments + shares; averages are per post):");
        foreach (var d in r.Dimensions)
        {
            sb.AppendLine($"By {d.Name}:");
            foreach (var c in d.Cells)
                sb.AppendLine($"- {c.Key}: {c.Posts} posts, {c.Measured} with engagement figures, average engagements {N(c.AvgEngagements)}, average views {N(c.AvgViews)}{(c.TooFew ? $" [TOO FEW: under {r.MinMeasured} measured posts, not evidence]" : "")}");
        }
        return sb.ToString();
    }

    /// <summary>Every figure in the text must be in the leaderboard (or be a small whole number such as a post number). Percentages are computations the planner must not make.</summary>
    public static List<string> FiguresNotInTable(IEnumerable<string?> texts, LeaderboardResult? table, IEnumerable<string?>? profileTexts = null)
    {
        var known = new HashSet<decimal>();
        // Numbers the verified product profile itself states (for example "up to 4 players") are facts, not invented results.
        foreach (var t in profileTexts ?? [])
            foreach (Match m in Regex.Matches(t ?? "", @"\d[\d,]*(?:\.\d+)?"))
                if (decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var pn)) known.Add(pn);
        if (table is not null)
        {
            known.Add(table.Published); known.Add(table.Measured); known.Add(table.MinMeasured);
            foreach (var c in table.Dimensions.SelectMany(d => d.Cells))
            {
                known.Add(c.Posts); known.Add(c.Measured); known.Add(c.WithViews);
                if (c.AvgEngagements is { } a) known.Add(a);
                if (c.AvgViews is { } v) known.Add(v);
            }
        }
        var bad = new HashSet<string>();
        foreach (var t in texts)
            foreach (Match m in Regex.Matches(t ?? "", @"\d[\d,]*(?:\.\d+)?%?"))
            {
                if (m.Value.EndsWith('%')) { bad.Add(m.Value); continue; }
                if (!decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) continue;
                if (d <= 60 && d == decimal.Truncate(d)) continue;
                if (!known.Contains(d)) bad.Add(m.Value);
            }
        return [.. bad];
    }
}
