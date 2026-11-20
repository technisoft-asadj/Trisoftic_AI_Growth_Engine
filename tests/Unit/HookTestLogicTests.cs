using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class HookTestLogicTests
{
    private static VariantFigures V(string label, string hook, int measured, decimal? avg) => new(label, hook, 3, measured, measured, avg, null);

    [Fact]
    public void A_variant_with_too_few_measured_posts_blocks_any_verdict()
    {
        var v = HookTestLogic.Evaluate([V("A", "Question", 3, 20), V("B", "Tip", 2, 5)]);
        Assert.Equal("NotEnoughPosts", v.Status); Assert.Null(v.Winner); Assert.Contains("variant B", v.Summary);
    }

    [Fact]
    public void A_clear_gap_over_enough_posts_declares_a_winner_with_the_figures_and_a_caveat()
    {
        var v = HookTestLogic.Evaluate([V("A", "Question", 3, 10), V("B", "Tip", 3, 16), V("C", "Challenge", 3, 9)]);
        Assert.Equal("Winner", v.Status); Assert.Equal("B", v.Winner);
        Assert.Contains("16", v.Summary); Assert.Contains("not proof", v.Summary);
    }

    [Theory]
    [InlineData(10, 11)]      // too close in relative terms
    [InlineData(1, 2.5)]      // a big relative jump but under the absolute gap of 2
    [InlineData(0, 1)]        // tiny numbers
    public void A_small_gap_is_no_clear_winner_and_that_is_a_normal_result(double a, double b)
    {
        var v = HookTestLogic.Evaluate([V("A", "Question", 4, (decimal)a), V("B", "Tip", 4, (decimal)b)]);
        Assert.Equal("NoClearWinner", v.Status); Assert.Null(v.Winner); Assert.Contains("No clear winner", v.Summary);
    }

    [Fact]
    public void An_exact_tie_has_no_winner_whichever_order_the_variants_come_in()
    {
        Assert.Equal("NoClearWinner", HookTestLogic.Evaluate([V("A", "Question", 3, 12), V("B", "Tip", 3, 12)]).Status);
        Assert.Equal("NoClearWinner", HookTestLogic.Evaluate([V("B", "Tip", 3, 12), V("A", "Question", 3, 12)]).Status);
    }

    [Fact]
    public void The_variants_take_turns_and_no_two_posts_share_a_day()
    {
        var layout = HookTestLogic.Layout(["Question", "Tip", "Challenge"], 3, new DateOnly(2026, 12, 1), 1);
        Assert.Equal(9, layout.Count);
        Assert.Equal("ABCABCABC", string.Concat(layout.Select(x => x.Label)));
        Assert.Equal(9, layout.Select(x => x.Day).Distinct().Count());
        Assert.Equal(new DateOnly(2026, 12, 9), layout[^1].Day);
        Assert.Equal(new DateOnly(2026, 12, 7), HookTestLogic.Layout(["Question", "Tip"], 3, new DateOnly(2026, 12, 1), 2)[3].Day);   // spacing follows the plan's cadence
    }

    [Fact]
    public void Tests_need_two_or_three_different_listed_hook_types_and_three_to_five_posts_each()
    {
        Assert.Empty(HookTestLogic.Validate(["Question", "Tip"], 3, "Opening", "Offline works"));
        Assert.NotEmpty(HookTestLogic.Validate(["Question"], 3, "n", "b"));
        Assert.NotEmpty(HookTestLogic.Validate(["Question", "Tip", "Challenge", "Scenario"], 3, "n", "b"));
        Assert.NotEmpty(HookTestLogic.Validate(["Question", "Question"], 3, "n", "b"));
        Assert.NotEmpty(HookTestLogic.Validate(["Question", "Clickbait"], 3, "n", "b"));
        Assert.NotEmpty(HookTestLogic.Validate(["Question", "Tip"], 2, "n", "b"));
        Assert.NotEmpty(HookTestLogic.Validate(["Question", "Tip"], 6, "n", "b"));
        Assert.NotEmpty(HookTestLogic.Validate(["Question", "Tip"], 3, "", "b"));
    }
}
