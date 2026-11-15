using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Budget;

public sealed record SpendTotals(decimal Recommended, decimal Approved, decimal Committed, decimal Actual)
{
    public static readonly SpendTotals Zero = new(0, 0, 0, 0);
    /// <summary>Money that is spent or promised. This is what the hard cap limits.</summary>
    public decimal Exposure => Committed + Actual;
    public static SpendTotals From(IEnumerable<(SpendKind Kind, decimal Amount)> rows)
    {
        var l = rows.ToList();
        decimal Sum(SpendKind k) => l.Where(r => r.Kind == k).Sum(r => r.Amount);
        return new(Sum(SpendKind.Recommended), Sum(SpendKind.Approved), Sum(SpendKind.Committed), Sum(SpendKind.Actual));
    }
}

public static class BudgetRules
{
    public const decimal MaxAmount = 1_000_000_000_000m;

    /// <summary>Why a spend record cannot be written, or null if it can. The cap is enforced here, in the backend, not in the screen.</summary>
    public static string? CheckSpend(BudgetStatus status, decimal cap, SpendTotals totals, SpendKind kind, decimal amount, decimal? allocationAmount, SpendTotals? allocationTotals)
    {
        if (amount <= 0 || amount > MaxAmount) return "The amount must be greater than zero";
        if (status == BudgetStatus.Closed) return "This budget is closed";
        if (kind != SpendKind.Recommended && status != BudgetStatus.Approved) return "Only recommendations can be recorded until the budget has been launched with an approval";

        if (kind == SpendKind.Approved && totals.Approved + amount > cap)
            return $"Approved amounts would reach {totals.Approved + amount:N2}, above the hard cap of {cap:N2}";
        if (kind is SpendKind.Committed or SpendKind.Actual && totals.Exposure + amount > cap)
            return $"Spent plus committed would reach {totals.Exposure + amount:N2}, above the hard cap of {cap:N2}. Raise the cap through an approved increase first";

        if (allocationAmount is { } plan && allocationTotals is { } at)
        {
            if (kind == SpendKind.Approved && at.Approved + amount > plan) return $"This line's approved amounts would reach {at.Approved + amount:N2}, above its allocation of {plan:N2}. Move money between lines with an approved reallocation";
            if (kind is SpendKind.Committed or SpendKind.Actual && at.Exposure + amount > plan) return $"This line's spent plus committed would reach {at.Exposure + amount:N2}, above its allocation of {plan:N2}. Move money between lines with an approved reallocation";
        }
        return null;
    }

    public static AlertLevel Level(decimal exposure, decimal cap, int warnPct, int stopPct)
    {
        if (cap <= 0) return AlertLevel.None;
        var pct = exposure * 100m / cap;
        return pct >= stopPct ? AlertLevel.Stop : pct >= warnPct ? AlertLevel.Warning : AlertLevel.None;
    }

    public static string? ValidateAllocations(decimal cap, IReadOnlyList<(string Label, decimal Amount)> allocations)
    {
        if (allocations.Any(a => string.IsNullOrWhiteSpace(a.Label) || a.Label.Length > 200)) return "Every allocation needs a name";
        if (allocations.Any(a => a.Amount <= 0 || a.Amount > MaxAmount)) return "Allocation amounts must be greater than zero";
        if (allocations.Select(a => a.Label.Trim().ToLowerInvariant()).Distinct().Count() != allocations.Count) return "Allocation names must be unique";
        if (allocations.Sum(a => a.Amount) > cap) return $"The allocations add up to {allocations.Sum(a => a.Amount):N2}, above the cap of {cap:N2}";
        return null;
    }

    public static string? ValidateThresholds(int warn, int stop) => warn is < 1 or > 100 || stop is < 1 or > 100 || warn >= stop ? "Warning must be below stop, both between 1 and 100 percent" : null;
}

// ------------------------------------------------------------------ planner ------------------------------------------------------------------

public sealed record WeeklyEvidence(string Channel, DateOnly Week, decimal Spend, decimal? Results);
public sealed record PlannerInput(decimal Cap, decimal Exposure, string Currency, RiskTolerance Risk, DateOnly Today, DateOnly PeriodEnd, string ResultMetric,
    IReadOnlyList<WeeklyEvidence> Evidence, IReadOnlyList<string> CandidateChannels, int UnusablePeriods, IReadOnlyList<string> ExcludedCurrencies);

public sealed record ChannelEvidence(string Channel, int WeeksUsed, decimal TotalSpend, decimal TotalResults, decimal? MedianCostPerResult, decimal? LowCostPerResult, decimal? HighCostPerResult, decimal MaxWeeklySpend, bool Qualified, string Note);
public sealed record ChannelPlan(string Channel, decimal Spend, int? ResultsLow, int? ResultsHigh, string Basis);
public sealed record Scenario(string Name, decimal Fraction, decimal TotalSpend, IReadOnlyList<ChannelPlan> Channels, string Confidence, IReadOnlyList<string> Assumptions, IReadOnlyList<string> Uncertainty);
public sealed record LearningExperiment(decimal Budget, int Days, IReadOnlyList<string> Channels, IReadOnlyList<string> Hypotheses, IReadOnlyList<string> StopGo, string? NeedsInput);
public sealed record PlanOutput(string Mode, string Headline, decimal Remaining, string Currency, string ResultMetric, IReadOnlyList<ChannelEvidence> Evidence, IReadOnlyList<Scenario> Scenarios,
    string? RecommendedScenario, LearningExperiment? Experiment, IReadOnlyList<string> Warnings);

/// <summary>
/// Proposes spend from the history actually recorded, and refuses to forecast when there is too little of it.
/// Rules, not a language model, produce the numbers so each one can be traced to recorded figures.
/// With thin history the output is a small capped learning experiment with stop/go rules, never a forecast.
/// </summary>
public static class BudgetPlanner
{
    public const int MinWeeks = 6;
    public const decimal MinResults = 30;
    public const decimal MaxChannelShare = 0.6m;

    public static PlanOutput Plan(PlannerInput i)
    {
        var remaining = Math.Max(0, i.Cap - i.Exposure);
        var warnings = new List<string>();
        if (i.ExcludedCurrencies.Count > 0) warnings.Add($"Spend recorded in {string.Join(", ", i.ExcludedCurrencies)} was left out because this budget is in {i.Currency}. Currencies are never converted or added.");
        if (i.UnusablePeriods > 0) warnings.Add($"{i.UnusablePeriods} recorded period(s) were longer than a week or lacked a matching result, so they could not be used.");
        if (remaining <= 0) return new PlanOutput("None", "There is no budget left to plan.", 0, i.Currency, i.ResultMetric, [], [], null, null, warnings);

        var evidence = Summarise(i);
        var qualified = evidence.Where(e => e.Qualified).ToList();
        if (qualified.Count == 0) return LearningPlan(i, remaining, evidence, warnings);

        var scenarios = new[] { ("Conservative", 0.5m), ("Balanced", 0.75m), ("Growth", 1.0m) }.Select(s => Scenario_(i, remaining, qualified, evidence, s.Item1, s.Item2)).ToList();
        return new PlanOutput("Forecast", $"Based on {qualified.Count} channel(s) with enough history. Results are ranges from past cost per result, not guarantees.", remaining, i.Currency, i.ResultMetric,
            evidence, scenarios, i.Risk.ToString(), null, warnings);
    }

    private static List<ChannelEvidence> Summarise(PlannerInput i)
    {
        var list = new List<ChannelEvidence>();
        foreach (var g in i.Evidence.GroupBy(e => e.Channel.Trim().ToLowerInvariant()))
        {
            var rows = g.Where(w => w.Spend > 0 && w.Results is > 0).ToList();
            var cpas = rows.Select(w => w.Spend / w.Results!.Value).OrderBy(x => x).ToList();
            var totalResults = rows.Sum(w => w.Results!.Value); var totalSpend = rows.Sum(w => w.Spend);
            var ok = rows.Count >= MinWeeks && totalResults >= MinResults;
            var note = ok ? $"{rows.Count} weeks, {totalResults:N0} results"
                : rows.Count < MinWeeks ? $"Only {rows.Count} week(s) with both spend and results; at least {MinWeeks} are needed"
                : $"Only {totalResults:N0} results in total; at least {MinResults:N0} are needed for a cost per result that is not mostly noise";
            list.Add(new ChannelEvidence(g.First().Channel.Length == 0 ? "(all channels)" : g.First().Channel, rows.Count, totalSpend, totalResults,
                cpas.Count == 0 ? null : Percentile(cpas, 0.5m), cpas.Count == 0 ? null : Percentile(cpas, 0.25m), cpas.Count == 0 ? null : Percentile(cpas, 0.75m),
                g.Any() ? g.Max(w => w.Spend) : 0, ok, note));
        }
        return list.OrderByDescending(e => e.Qualified).ThenBy(e => e.Channel).ToList();
    }

    public static decimal Percentile(IReadOnlyList<decimal> sorted, decimal p)
    {
        if (sorted.Count == 1) return sorted[0];
        var pos = p * (sorted.Count - 1); var lo = (int)Math.Floor(pos); var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static Scenario Scenario_(PlannerInput i, decimal remaining, List<ChannelEvidence> qualified, List<ChannelEvidence> all, string name, decimal fraction)
    {
        var total = Math.Round(remaining * fraction, 2);
        var explore = i.Risk switch { RiskTolerance.Conservative => 0.05m, RiskTolerance.Growth => 0.15m, _ => 0.10m };
        var untested = i.CandidateChannels.Where(c => !qualified.Any(q => q.Channel.Equals(c, StringComparison.OrdinalIgnoreCase))).Take(3).ToList();
        var exploreSpend = untested.Count == 0 ? 0m : Math.Round(total * explore, 2);
        var proven = total - exploreSpend;

        // Cheaper channels get more, but no proven channel gets more than 60% (unless it is the only one).
        var weights = qualified.ToDictionary(q => q.Channel, q => 1m / q.MedianCostPerResult!.Value);
        var share = weights.ToDictionary(w => w.Key, w => w.Value / weights.Values.Sum());
        if (qualified.Count > 1)
            for (var pass = 0; pass < 5 && share.Any(s => s.Value > MaxChannelShare + 0.0001m); pass++)
            {
                var over = share.Where(s => s.Value > MaxChannelShare).Select(s => s.Key).ToList();
                var spare = over.Sum(k => share[k] - MaxChannelShare);
                foreach (var k in over) share[k] = MaxChannelShare;
                var under = share.Keys.Where(k => !over.Contains(k)).ToList();
                var uw = under.Sum(k => weights[k]);
                foreach (var k in under) share[k] += spare * weights[k] / uw;
            }

        var weeks = Math.Max(1, (int)Math.Ceiling((i.PeriodEnd.DayNumber - i.Today.DayNumber) / 7.0));
        var plans = new List<ChannelPlan>(); var beyondHistory = false;
        foreach (var q in qualified)
        {
            var spend = Math.Round(proven * share[q.Channel], 2);
            var low = q.HighCostPerResult is > 0 ? (int)Math.Floor(spend / q.HighCostPerResult.Value) : (int?)null;
            var high = q.LowCostPerResult is > 0 ? (int)Math.Ceiling(spend / q.LowCostPerResult.Value) : (int?)null;
            if (spend / weeks > q.MaxWeeklySpend * 2) beyondHistory = true;
            plans.Add(new ChannelPlan(q.Channel, spend, low, high, $"Past cost per result {q.LowCostPerResult:N2} to {q.HighCostPerResult:N2} (middle {q.MedianCostPerResult:N2}) over {q.WeeksUsed} weeks"));
        }
        foreach (var c in untested) plans.Add(new ChannelPlan(c, Math.Round(exploreSpend / untested.Count, 2), null, null, "Learning spend: no forecast, not enough history for this channel"));

        var weakest = qualified.Min(q => q.WeeksUsed); var fewest = qualified.Min(q => q.TotalResults);
        var confidence = !beyondHistory && weakest >= 12 && fewest >= 100 ? "Medium" : "Low";
        var assumptions = new List<string>
        {
            "Each channel keeps roughly the cost per result it had in the weeks used.",
            "No seasonality, launches or competitor changes are assumed.",
            $"Only spend recorded in {i.Currency} and the '{i.ResultMetric}' result were used.",
        };
        var uncertainty = new List<string> { "A range is shown, not a single number: it spans the middle half of the weekly costs seen so far." };
        if (beyondHistory) uncertainty.Add("This plan spends more than twice what any single week has carried before. Costs usually rise as spend grows, so treat the results as optimistic.");
        if (confidence == "Low") uncertainty.Add("History is short or thin for at least one channel. Start smaller, review weekly, and stop a channel if its cost per result rises.");
        return new Scenario(name, fraction, total, plans, confidence, assumptions, uncertainty);
    }

    private static PlanOutput LearningPlan(PlannerInput i, decimal remaining, List<ChannelEvidence> evidence, List<string> warnings)
    {
        var pct = i.Risk switch { RiskTolerance.Conservative => 0.05m, RiskTolerance.Growth => 0.15m, _ => 0.10m };
        var budget = Math.Round(remaining * pct, 2);
        var channels = i.CandidateChannels.Take(3).ToList();
        var days = Math.Clamp(i.PeriodEnd.DayNumber - i.Today.DayNumber, 1, 14);
        var why = evidence.Count == 0
            ? "No spend and result history has been recorded for this product yet, so any forecast would be invented."
            : "The recorded history is too thin for any channel: " + string.Join("; ", evidence.Select(e => $"{e.Channel}: {e.Note}"));
        var hypotheses = channels.Select(c => $"{c} can bring '{i.ResultMetric}' at a cost per result we would accept.").ToList();
        var stopGo = new List<string>
        {
            $"Stop all learning spend when {budget:N2} {i.Currency} has been spent, or after {days} days, whichever comes first.",
            "Before starting, write down the highest cost per result you would accept. History is too thin to suggest one.",
            $"Go (scale a channel) only if it delivered at least {MinResults:N0} results at an acceptable cost per result. Fewer results than that is noise, not a trend.",
            "Stop a channel early if it has spent a third of its share with no results at all.",
            "Record the spend and the results every week so the next plan can use them.",
        };
        string? needs = channels.Count == 0 ? "Add the channels you want to test as allocation lines on this budget, then plan again." : null;
        return new PlanOutput("LearningExperiment", $"There is not enough history to forecast. Run a small capped experiment of {budget:N2} {i.Currency} instead. {why}", remaining, i.Currency, i.ResultMetric,
            evidence, [], null, new LearningExperiment(budget, days, channels, hypotheses, stopGo, needs), warnings);
    }

    /// <summary>Monday of the week containing the date, so spend and results from different sources line up.</summary>
    public static DateOnly WeekOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}
