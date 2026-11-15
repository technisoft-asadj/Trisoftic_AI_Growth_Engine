using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Budget;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class BudgetRulesTests
{
    private static SpendTotals T(decimal rec = 0, decimal app = 0, decimal com = 0, decimal act = 0) => new(rec, app, com, act);

    // ---- cap enforcement ----

    [Fact]
    public void Spent_plus_committed_may_never_pass_the_hard_cap_but_recommendations_cost_nothing()
    {
        Assert.Null(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(com: 300, act: 600), SpendKind.Actual, 100, null, null));          // exactly reaches the cap
        Assert.NotNull(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(com: 300, act: 600), SpendKind.Actual, 100.01m, null, null));    // one cent over
        Assert.NotNull(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(com: 300, act: 600), SpendKind.Committed, 200, null, null));
        Assert.Null(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(app: 5000, com: 300, act: 600), SpendKind.Recommended, 50_000, null, null));   // a suggestion is not spending
    }

    [Fact]
    public void Approved_amounts_have_their_own_ceiling_and_the_four_states_are_not_added_together()
    {
        Assert.NotNull(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(app: 900), SpendKind.Approved, 101, null, null));
        Assert.Null(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(rec: 800, app: 900, com: 0, act: 0), SpendKind.Committed, 1000, null, null));   // approved/recommended totals do not eat into exposure
    }

    [Fact]
    public void A_draft_or_closed_budget_accepts_only_what_it_should()
    {
        Assert.NotNull(BudgetRules.CheckSpend(BudgetStatus.Draft, 1000, T(), SpendKind.Actual, 10, null, null));          // nothing real before launch
        Assert.NotNull(BudgetRules.CheckSpend(BudgetStatus.Draft, 1000, T(), SpendKind.Committed, 10, null, null));
        Assert.Null(BudgetRules.CheckSpend(BudgetStatus.Draft, 1000, T(), SpendKind.Recommended, 10, null, null));
        Assert.NotNull(BudgetRules.CheckSpend(BudgetStatus.Closed, 1000, T(), SpendKind.Recommended, 10, null, null));
    }

    [Theory]
    [InlineData(0)] [InlineData(-5)] [InlineData(2_000_000_000_000)]
    public void Silly_amounts_are_refused(double amount)
        => Assert.NotNull(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(), SpendKind.Actual, (decimal)amount, null, null));

    [Fact]
    public void An_allocation_line_has_its_own_limit()
    {
        Assert.Null(BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(act: 100), SpendKind.Actual, 100, 300, T(act: 200)));
        var msg = BudgetRules.CheckSpend(BudgetStatus.Approved, 1000, T(act: 100), SpendKind.Actual, 101, 300, T(act: 200));
        Assert.NotNull(msg); Assert.Contains("reallocation", msg);
    }

    // ---- thresholds ----

    [Theory]
    [InlineData(0, AlertLevel.None)] [InlineData(799, AlertLevel.None)] [InlineData(800, AlertLevel.Warning)] [InlineData(999, AlertLevel.Warning)] [InlineData(1000, AlertLevel.Stop)]
    public void Warning_and_stop_levels_follow_the_percentages(int exposure, AlertLevel expected) => Assert.Equal(expected, BudgetRules.Level(exposure, 1000, 80, 100));

    [Fact]
    public void Threshold_and_allocation_validation()
    {
        Assert.NotNull(BudgetRules.ValidateThresholds(90, 80)); Assert.NotNull(BudgetRules.ValidateThresholds(0, 100)); Assert.Null(BudgetRules.ValidateThresholds(80, 100));
        Assert.Null(BudgetRules.ValidateAllocations(1000, [("FB", 600), ("IG", 400)]));
        Assert.NotNull(BudgetRules.ValidateAllocations(1000, [("FB", 600), ("IG", 401)]));      // over the cap
        Assert.NotNull(BudgetRules.ValidateAllocations(1000, [("FB", 600), ("fb", 100)]));      // duplicate name
        Assert.NotNull(BudgetRules.ValidateAllocations(1000, [("", 100)]));
        Assert.NotNull(BudgetRules.ValidateAllocations(1000, [("FB", 0)]));
    }

    // ---- planner ----

    private static readonly DateOnly Today = new(2026, 11, 16);   // a Monday
    private static PlannerInput Input(IReadOnlyList<WeeklyEvidence> ev, IReadOnlyList<string>? channels = null, RiskTolerance risk = RiskTolerance.Balanced, decimal cap = 10_000, decimal exposure = 0)
        => new(cap, exposure, "USD", risk, Today, Today.AddDays(56), "leads", ev, channels ?? ["facebook", "google"], 0, []);
    private static List<WeeklyEvidence> Weeks(string channel, int n, decimal spend, decimal results, decimal jitter = 0)
        => Enumerable.Range(1, n).Select(i => new WeeklyEvidence(channel, Today.AddDays(-7 * i), spend + (i % 2 == 0 ? jitter : -jitter), results)).ToList();

    [Fact]
    public void With_no_history_the_planner_proposes_a_capped_learning_experiment_and_no_forecast()
    {
        var p = BudgetPlanner.Plan(Input([]));
        Assert.Equal("LearningExperiment", p.Mode);
        Assert.Empty(p.Scenarios); Assert.Null(p.RecommendedScenario);
        Assert.NotNull(p.Experiment);
        Assert.Equal(1000m, p.Experiment!.Budget);                               // 10% of 10,000 for a balanced risk appetite
        Assert.Equal(["facebook", "google"], p.Experiment.Channels);
        Assert.Contains(p.Experiment.StopGo, s => s.Contains("Stop all learning spend"));
        Assert.Contains("invented", p.Headline);
    }

    [Theory]
    [InlineData(RiskTolerance.Conservative, 500)] [InlineData(RiskTolerance.Balanced, 1000)] [InlineData(RiskTolerance.Growth, 1500)]
    public void The_learning_budget_scales_with_risk_appetite_and_never_exceeds_what_is_left(RiskTolerance risk, int expected)
    {
        Assert.Equal(expected, BudgetPlanner.Plan(Input([], risk: risk)).Experiment!.Budget);
        Assert.Equal(40m, BudgetPlanner.Plan(Input([], risk: RiskTolerance.Balanced, cap: 10_000, exposure: 9_600)).Experiment!.Budget);   // 10% of the 400 left
    }

    [Fact]
    public void Too_few_weeks_or_too_few_results_still_means_no_forecast()
    {
        Assert.Equal("LearningExperiment", BudgetPlanner.Plan(Input(Weeks("facebook", 5, 500, 20))).Mode);          // 5 weeks, 100 results: not enough weeks
        var thin = BudgetPlanner.Plan(Input(Weeks("facebook", 8, 500, 3)));                                         // 8 weeks, only 24 results
        Assert.Equal("LearningExperiment", thin.Mode);
        Assert.Contains(thin.Evidence, e => !e.Qualified && e.Note.Contains("results"));
        Assert.Equal("LearningExperiment", BudgetPlanner.Plan(Input(Weeks("facebook", 10, 500, 0))).Mode);          // spend but never a result
    }

    [Fact]
    public void Without_candidate_channels_the_experiment_asks_for_input_instead_of_inventing_them()
    {
        var p = BudgetPlanner.Plan(Input([], channels: []));
        Assert.Equal("LearningExperiment", p.Mode);
        Assert.NotNull(p.Experiment!.NeedsInput);
    }

    [Fact]
    public void Enough_history_gives_three_scenarios_with_ranges_assumptions_and_evidence()
    {
        var p = BudgetPlanner.Plan(Input(Weeks("facebook", 10, 500, 25, jitter: 50)));
        Assert.Equal("Forecast", p.Mode);
        Assert.Equal(["Conservative", "Balanced", "Growth"], p.Scenarios.Select(s => s.Name));
        Assert.Equal("Balanced", p.RecommendedScenario);
        Assert.All(p.Scenarios, s => { Assert.NotEmpty(s.Assumptions); Assert.NotEmpty(s.Uncertainty); Assert.True(s.TotalSpend <= 10_000m); });
        Assert.True(p.Scenarios[0].TotalSpend < p.Scenarios[1].TotalSpend && p.Scenarios[1].TotalSpend < p.Scenarios[2].TotalSpend);
        var fb = p.Scenarios[1].Channels.Single(c => c.Channel == "facebook");
        Assert.True(fb.ResultsLow < fb.ResultsHigh);                                  // a range, not a point
        var ev = p.Evidence.Single(e => e.Channel == "facebook");
        Assert.True(ev.Qualified); Assert.Equal(10, ev.WeeksUsed); Assert.Equal(250m, ev.TotalResults);
    }

    [Fact]
    public void Untested_channels_get_only_learning_spend_with_no_results_promised()
    {
        var p = BudgetPlanner.Plan(Input(Weeks("facebook", 10, 500, 25), channels: ["facebook", "tiktok"]));
        var plan = p.Scenarios[1].Channels;
        var tiktok = plan.Single(c => c.Channel == "tiktok");
        Assert.Null(tiktok.ResultsLow); Assert.Null(tiktok.ResultsHigh);
        Assert.Contains("no forecast", tiktok.Basis);
        Assert.True(tiktok.Spend < plan.Single(c => c.Channel == "facebook").Spend);
    }

    [Fact]
    public void No_proven_channel_gets_more_than_sixty_percent_and_cheaper_channels_get_more()
    {
        var ev = Weeks("facebook", 10, 500, 50).Concat(Weeks("google", 10, 500, 25)).Concat(Weeks("linkedin", 10, 500, 24)).ToList();   // facebook is twice as cheap
        var p = BudgetPlanner.Plan(Input(ev, channels: ["facebook", "google", "linkedin"]));
        var s = p.Scenarios[2];   // growth: all of the remaining budget
        var spend = s.Channels.ToDictionary(c => c.Channel, c => c.Spend);
        Assert.True(spend["facebook"] > spend["google"]);
        Assert.All(spend.Values, v => Assert.True(v <= s.TotalSpend * 0.6m + 0.05m));
        Assert.Equal(s.TotalSpend, spend.Values.Sum(), 1);
    }

    [Fact]
    public void Spending_far_beyond_anything_seen_before_is_flagged_and_confidence_stays_low()
    {
        var p = BudgetPlanner.Plan(Input(Weeks("facebook", 14, 100, 10)));            // history is 100 a week; the plan asks for thousands
        Assert.All(p.Scenarios, s => Assert.Equal("Low", s.Confidence));
        Assert.Contains(p.Scenarios[2].Uncertainty, u => u.Contains("more than twice"));
    }

    [Fact]
    public void Plentiful_stable_history_can_reach_medium_confidence_but_never_high()
    {
        var p = BudgetPlanner.Plan(new PlannerInput(10_000, 0, "USD", RiskTolerance.Balanced, Today, Today.AddDays(70), "leads", Weeks("facebook", 16, 800, 20), ["facebook"], 0, []));
        Assert.Contains(p.Scenarios, s => s.Confidence == "Medium");
        Assert.DoesNotContain(p.Scenarios, s => s.Confidence == "High");
    }

    [Fact]
    public void An_exhausted_budget_gets_no_plan_and_excluded_currencies_are_reported()
    {
        Assert.Equal("None", BudgetPlanner.Plan(Input([], exposure: 10_000)).Mode);
        var p = BudgetPlanner.Plan(new PlannerInput(10_000, 0, "USD", RiskTolerance.Balanced, Today, Today.AddDays(30), "leads", [], ["facebook"], 2, ["PKR"]));
        Assert.Contains(p.Warnings, w => w.Contains("PKR") && w.Contains("never converted"));
        Assert.Contains(p.Warnings, w => w.Contains("2 recorded period"));
    }

    [Fact]
    public void Weeks_start_on_monday_and_percentiles_interpolate()
    {
        Assert.Equal(new DateOnly(2026, 11, 16), BudgetPlanner.WeekOf(new DateOnly(2026, 11, 22)));   // Sunday belongs to the week that began Monday the 16th
        Assert.Equal(new DateOnly(2026, 11, 16), BudgetPlanner.WeekOf(new DateOnly(2026, 11, 16)));
        Assert.Equal(25m, BudgetPlanner.Percentile([10m, 20m, 30m, 40m], 0.5m));
        Assert.Equal(17.5m, BudgetPlanner.Percentile([10m, 20m, 30m, 40m], 0.25m));
        Assert.Equal(7m, BudgetPlanner.Percentile([7m], 0.9m));
    }
}
