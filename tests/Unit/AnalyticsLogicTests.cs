using TriSoftic.GrowthEngine.Modules.Analytics;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class AnalyticsLogicTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly IReadOnlyDictionary<string, MetricDefinition> Defs = BuiltInMetrics.All.ToDictionary(d => d.Key);

    private static List<string[]> Sheet(params string[] lines) => lines.Select(l => l.Split(',')).ToList();
    private static (List<ParsedRow> Rows, List<RowError> Errors, string? Problem) Parse(params string[] lines) => ImportParser.Parse(Sheet(lines), Defs, Today);

    // ---- import validation ----

    [Fact]
    public void A_clean_sheet_becomes_observations_and_a_blank_value_is_unavailable_not_zero()
    {
        var (rows, errors, problem) = Parse("metric,periodStart,periodEnd,value,channel", "impressions,2026-11-01,2026-11-07,12000,facebook", "reach,2026-11-01,2026-11-07,,facebook", "clicks,2026-11-01,,n/a,");
        Assert.Null(problem); Assert.Empty(errors);
        Assert.Equal(3, rows.Count);
        Assert.Equal(12000m, rows[0].Value);
        Assert.Null(rows[1].Value);                       // blank
        Assert.Null(rows[2].Value);                       // "n/a"
        Assert.Equal(new DateOnly(2026, 11, 1), rows[2].End);   // no end given: a one-day period
    }

    [Fact]
    public void Bad_rows_are_reported_with_their_line_number_and_the_good_ones_still_come_through()
    {
        var (rows, errors, _) = Parse("metric,periodStart,value,currency",
            "impressions,2026-11-01,100,",           // line 2 ok
            "nonsense,2026-11-01,5,",                // line 3 unknown metric
            "impressions,01/11/2026,5,",             // line 4 bad date format
            "impressions,2026-11-02,abc,",           // line 5 not a number
            "impressions,2026-11-03,-4,",            // line 6 negative
            "ctr,2026-11-03,140,",                   // line 7 percent above 100
            "spend,2026-11-03,50,",                  // line 8 money without currency
            "impressions,2026-11-03,50,USD",         // line 9 currency on a non-money metric
            "impressions,2026-12-31,5,",             // line 10 the future
            "spend,2026-11-04,50,usd");              // line 11 ok (currency upper-cased)
        Assert.Equal([2, 11], rows.Select(r => r.Row));
        Assert.Equal("USD", rows[1].Currency);
        Assert.Equal([3, 4, 5, 6, 7, 8, 9, 10], errors.Select(e => e.Row));
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }

    [Fact]
    public void Duplicate_rows_in_one_file_and_inverted_periods_are_rejected()
    {
        var (rows, errors, _) = Parse("metric,periodStart,periodEnd,value,channel",
            "impressions,2026-11-01,2026-11-07,10,fb", "impressions,2026-11-01,2026-11-07,11,FB", "impressions,2026-11-08,2026-11-02,5,fb", "impressions,2025-01-01,2026-11-01,5,fb");
        Assert.Single(rows);
        Assert.Equal(3, errors.Count);
        Assert.Contains("Duplicate", errors[0].Message);
    }

    [Fact]
    public void Files_without_the_needed_columns_or_with_too_many_rows_are_refused_as_a_whole()
    {
        Assert.NotNull(Parse("foo,bar", "1,2").Problem);
        Assert.NotNull(Parse("metric,value").Problem);                 // header only
        var big = new List<string[]> { new[] { "metric", "periodStart", "value" } };
        for (var i = 0; i < ImportParser.MaxRows + 1; i++) big.Add(["impressions", "2026-11-01", "1"]);
        Assert.NotNull(ImportParser.Parse(big, Defs, Today).FileProblem);
    }

    // ---- tiles ----

    private static readonly Guid SrcA = Guid.NewGuid(), SrcB = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private static Obs O(decimal? v, int day, Guid? src = null, string cur = "", bool stale = false, DateTimeOffset? at = null, string name = "A")
        => new(src ?? SrcA, name, new DateOnly(2026, 11, day), new DateOnly(2026, 11, day), v, cur, "", "", Guid.Empty, at ?? T0, stale);

    [Fact]
    public void No_observations_is_NoData_with_no_value_never_zero()
    {
        var t = TileBuilder.Build(Defs["impressions"], []);
        Assert.Equal("NoData", t.Status); Assert.Null(t.Value); Assert.Empty(t.ValuesByCurrency); Assert.Equal(0, t.Periods); Assert.Null(t.OldestRetrieval);
    }

    [Fact]
    public void Only_unavailable_observations_are_Unavailable_and_some_unavailable_is_Partial()
    {
        Assert.Equal("Unavailable", TileBuilder.Build(Defs["impressions"], [O(null, 1), O(null, 2)]).Status);
        Assert.Null(TileBuilder.Build(Defs["impressions"], [O(null, 1)]).Value);
        var partial = TileBuilder.Build(Defs["impressions"], [O(100, 1), O(null, 2), O(50, 3)]);
        Assert.Equal("Partial", partial.Status); Assert.Equal(150m, partial.Value); Assert.Equal(1, partial.UnavailablePeriods); Assert.Equal(3, partial.Periods);
        Assert.Equal(0m, TileBuilder.Build(Defs["impressions"], [O(0, 1)]).Value);        // a measured zero is a real zero
    }

    [Fact]
    public void Money_is_reported_per_currency_and_never_added_across_currencies()
    {
        var t = TileBuilder.Build(Defs["spend"], [O(100, 1, cur: "USD"), O(50, 2, cur: "USD"), O(9000, 1, cur: "PKR")]);
        Assert.Null(t.Value);
        Assert.Equal([new CurrencyTotal("PKR", 9000), new CurrencyTotal("USD", 150)], t.ValuesByCurrency);
    }

    [Fact]
    public void Levels_use_the_latest_figure_and_unique_counts_are_not_summed()
    {
        var followers = TileBuilder.Build(Defs["followers"], [O(1000, 1), O(1100, 8), O(1050, 4)]);
        Assert.Equal(1100m, followers.Value);                                   // latest period, not the sum
        var reach = TileBuilder.Build(Defs["reach"], [O(500, 1), O(600, 2)]);
        Assert.Null(reach.Value);                                              // unique reach cannot be added across periods
        Assert.Equal(600m, reach.Latest); Assert.Equal(new DateOnly(2026, 11, 2), reach.LatestPeriodEnd);
    }

    [Fact]
    public void Two_sources_for_one_figure_are_not_double_counted_and_a_disagreement_is_flagged()
    {
        var older = T0; var newer = T0.AddDays(1);
        var t = TileBuilder.Build(Defs["impressions"], [O(100, 1, SrcA, at: older, name: "Manual"), O(120, 1, SrcB, at: newer, name: "Platform"), O(50, 2, SrcA, at: older, name: "Manual"), O(50, 2, SrcB, at: newer, name: "Platform")]);
        Assert.Equal(170m, t.Value);                // 120 (newest for day 1) + 50 (agreeing duplicates counted once)
        Assert.Equal(1, t.Conflicts);               // only day 1 disagreed
        Assert.Equal(["Platform"], t.Sources);      // the sources whose figures were actually used
    }

    [Fact]
    public void A_tile_is_stale_if_any_source_behind_it_is_stale_and_reports_the_oldest_retrieval()
    {
        var t = TileBuilder.Build(Defs["impressions"], [O(1, 1, stale: false, at: T0.AddDays(2)), O(1, 2, SrcB, stale: true, at: T0)]);
        Assert.True(t.Stale); Assert.Equal(T0, t.OldestRetrieval);
        Assert.False(TileBuilder.Build(Defs["impressions"], [O(1, 1)]).Stale);
    }

    [Fact]
    public void Freshness_uses_the_threshold_and_treats_never_retrieved_as_stale()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.False(Freshness.IsStale(now.AddHours(-47), 48, now));
        Assert.True(Freshness.IsStale(now.AddHours(-49), 48, now));
        Assert.True(Freshness.IsStale(null, 48, now));
    }

    [Fact]
    public void Every_built_in_metric_has_a_definition_text_and_unique_key()
    {
        Assert.All(BuiltInMetrics.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Definition)));
        Assert.Equal(BuiltInMetrics.All.Count, BuiltInMetrics.All.Select(m => m.Key).Distinct().Count());
    }
}
