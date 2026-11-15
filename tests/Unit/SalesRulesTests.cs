using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.Modules.Crm;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class OpportunityRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Pipe = Guid.NewGuid();
    private static PipelineStage Stage(StageKind k, int prob = 20) => new() { PipelineId = Pipe, Kind = k, Probability = prob, Name = k.ToString() };
    private static Opportunity Open(PipelineStage at, decimal value = 1000) => new() { PipelineId = Pipe, StageId = at.Id, Value = value };

    [Fact]
    public void Winning_records_revenue_defaulting_to_the_quoted_value_and_closes_the_deal()
    {
        var s0 = Stage(StageKind.Open); var won = Stage(StageKind.Won);
        var o = Open(s0, 1200);
        OpportunityRules.Move(o, won, null, null, Now);
        Assert.Equal(OpportunityStatus.Won, o.Status);
        Assert.Equal(1200, o.RevenueAmount);
        Assert.Equal(Now, o.ClosedAt);
        Assert.Equal(won.Id, o.StageId);

        var o2 = Open(s0); OpportunityRules.Move(o2, won, "Great fit", 900, Now);
        Assert.Equal(900, o2.RevenueAmount);       // actual revenue can differ from the quote
        Assert.Throws<IllegalTransitionException>(() => OpportunityRules.Move(Open(s0), won, null, -1, Now));
    }

    [Fact]
    public void Losing_requires_a_reason_and_clears_revenue()
    {
        var s0 = Stage(StageKind.Open); var lost = Stage(StageKind.Lost);
        Assert.Throws<IllegalTransitionException>(() => OpportunityRules.Move(Open(s0), lost, " ", null, Now));
        var o = Open(s0); OpportunityRules.Move(o, lost, "Chose a competitor", null, Now);
        Assert.Equal(OpportunityStatus.Lost, o.Status);
        Assert.Null(o.RevenueAmount);
        Assert.Equal("Chose a competitor", o.WonLostReason);
    }

    [Fact]
    public void Closed_deals_cannot_move_and_cross_pipeline_or_no_op_moves_are_refused()
    {
        var s0 = Stage(StageKind.Open); var s1 = Stage(StageKind.Open); var won = Stage(StageKind.Won);
        var o = Open(s0); OpportunityRules.Move(o, won, null, null, Now);
        Assert.Throws<IllegalTransitionException>(() => OpportunityRules.Move(o, s1, null, null, Now));

        var fresh = Open(s0);
        Assert.Throws<IllegalTransitionException>(() => OpportunityRules.Move(fresh, s0, null, null, Now));                                  // same stage
        var other = new PipelineStage { PipelineId = Guid.NewGuid(), Kind = StageKind.Open };
        Assert.Throws<IllegalTransitionException>(() => OpportunityRules.Move(fresh, other, null, null, Now));                               // other pipeline
        OpportunityRules.Move(fresh, s1, null, null, Now);
        Assert.Equal(OpportunityStatus.Open, fresh.Status);
    }

    [Fact]
    public void Reopen_only_into_an_open_stage_and_resets_the_outcome()
    {
        var s0 = Stage(StageKind.Open); var won = Stage(StageKind.Won);
        var o = Open(s0); OpportunityRules.Move(o, won, "ok", 500, Now);
        Assert.Throws<IllegalTransitionException>(() => OpportunityRules.Reopen(o, won, Now));
        OpportunityRules.Reopen(o, s0, Now.AddDays(1));
        Assert.Equal(OpportunityStatus.Open, o.Status);
        Assert.Null(o.RevenueAmount); Assert.Null(o.ClosedAt); Assert.Null(o.WonLostReason);
        Assert.Throws<IllegalTransitionException>(() => OpportunityRules.Reopen(o, s0, Now));   // already open
    }

    [Fact]
    public void Probability_is_fixed_at_the_ends_and_overridable_in_between()
    {
        var open = Stage(StageKind.Open, 40);
        var o = Open(open);
        Assert.Equal(40, OpportunityRules.EffectiveProbability(o, open));
        o.ProbabilityOverride = 65;
        Assert.Equal(65, OpportunityRules.EffectiveProbability(o, open));
        Assert.Equal(100, OpportunityRules.EffectiveProbability(o, Stage(StageKind.Won)));
        Assert.Equal(0, OpportunityRules.EffectiveProbability(o, Stage(StageKind.Lost)));
    }

    [Fact]
    public void Stage_lists_must_have_one_won_one_lost_and_an_open_stage()
    {
        (string, StageKind, int) S(string n, StageKind k, int p = 10) => (n, k, p);
        Assert.Null(OpportunityRules.ValidateStages([S("A", StageKind.Open), S("Won", StageKind.Won, 100), S("Lost", StageKind.Lost, 0)]));
        Assert.NotNull(OpportunityRules.ValidateStages([S("A", StageKind.Open), S("Won", StageKind.Won)]));                                              // too few / no lost
        Assert.NotNull(OpportunityRules.ValidateStages([S("A", StageKind.Open), S("Won", StageKind.Won), S("Won2", StageKind.Won), S("Lost", StageKind.Lost)]));
        Assert.NotNull(OpportunityRules.ValidateStages([S("A", StageKind.Won), S("B", StageKind.Lost), S("C", StageKind.Lost)]));
        Assert.NotNull(OpportunityRules.ValidateStages([S("A", StageKind.Open), S("a", StageKind.Open), S("Won", StageKind.Won), S("Lost", StageKind.Lost)])); // duplicate names
        Assert.NotNull(OpportunityRules.ValidateStages([S("A", StageKind.Open, 120), S("Won", StageKind.Won), S("Lost", StageKind.Lost)]));
    }
}

public sealed class DashboardMathTests
{
    [Fact]
    public void Rates_are_null_not_zero_when_there_is_nothing_to_divide()
    {
        Assert.Null(DashboardMath.Rate(0, 0));
        Assert.Equal(0.0, DashboardMath.Rate(0, 5));      // a measured zero is a real zero
        Assert.Equal(33.3, DashboardMath.Rate(1, 3));
        Assert.Equal(100.0, DashboardMath.Rate(2, 2));
    }

    [Fact]
    public void Mean_and_median_are_null_for_no_samples_and_correct_otherwise()
    {
        Assert.Null(DashboardMath.Mean([])); Assert.Null(DashboardMath.Median([]));
        Assert.Equal(0.0, DashboardMath.Mean([0, 0]));
        Assert.Equal(2.0, DashboardMath.Median([1, 2, 3]));
        Assert.Equal(2.5, DashboardMath.Median([1, 2, 3, 4]));
        Assert.Equal(2.0, DashboardMath.Mean([1, 2, 3]));
    }
}
