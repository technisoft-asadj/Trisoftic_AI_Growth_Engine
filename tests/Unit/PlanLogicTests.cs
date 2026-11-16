using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class PlanLogicTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static PlanSpec Spec(int days = 7, int every = 1, DateOnly? start = null) => new("Launch week", "Introduce the game", "Facebook", "en", start ?? Today, days, every, new TimeOnly(13, 0));

    // ---- cadence ----

    [Theory]
    [InlineData(3, 1, 3)]
    [InlineData(7, 1, 7)]
    [InlineData(30, 2, 15)]
    [InlineData(30, 7, 5)]
    [InlineData(1, 1, 1)]
    public void The_number_of_posts_follows_the_cadence(int days, int every, int expected) => Assert.Equal(expected, PlanLogic.SlotCount(days, every));

    [Fact]
    public void Post_times_are_worked_out_by_code_in_utc_one_per_step()
    {
        var times = PlanLogic.SlotTimes(Spec(days: 7, every: 3));   // days 0, 3, 6
        Assert.Equal([new DateTimeOffset(2026, 11, 16, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 19, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 22, 13, 0, 0, TimeSpan.Zero)], times);
    }

    [Fact]
    public void A_plan_cannot_start_in_the_past_or_be_unreasonably_large()
    {
        Assert.Empty(PlanLogic.Validate(Spec(), Today));
        Assert.Contains(PlanLogic.Validate(Spec(start: Today.AddDays(-1)), Today), e => e.Contains("past"));
        Assert.Contains(PlanLogic.Validate(Spec(days: 60, every: 1), Today), e => e.Contains("more than"));   // 60 posts
        Assert.Contains(PlanLogic.Validate(Spec(days: 0), Today), e => e.Contains("between 1 and 60"));
        Assert.Contains(PlanLogic.Validate(Spec() with { Goal = " " }, Today), e => e.Contains("goal"));
    }

    // ---- the planner's answer ----

    [Fact]
    public void A_plan_must_have_exactly_the_requested_posts_with_distinct_angles()
    {
        var ok = new PlanDraft { Rationale = "why", Measures = [new() { Name = "Reactions", Why = "interest" }], Slots = [new() { Angle = "Intro", Brief = "b" }, new() { Angle = "Modes", Brief = "b" }] };
        Assert.Empty(PlanLogic.ValidatePlan(ok, 2));
        Assert.Contains(PlanLogic.ValidatePlan(ok, 3), v => v.Contains("exactly 3"));
        ok.Slots[1].Angle = " intro ";
        Assert.Contains(PlanLogic.ValidatePlan(ok, 2), v => v.Contains("different angle"));
    }

    [Fact]
    public void A_plan_answer_with_surrounding_text_still_parses_and_garbage_does_not()
    {
        var p = PlanLogic.ParsePlan("Sure! {\"rationale\":\"r\",\"measures\":[{\"name\":\"Reactions\",\"why\":\"w\"}],\"slots\":[{\"angle\":\"A\",\"brief\":\"B\"}]} Done.", out var err);
        Assert.Null(err); Assert.Single(p!.Slots);
        Assert.Null(PlanLogic.ParsePlan("no json here", out var e2)); Assert.NotNull(e2);
    }

    // ---- reviewing results honestly ----

    private static SlotFigures Fig(int pos, int? r, int? c, int? s, long? views = null) => new(pos, "Angle " + pos, DateTimeOffset.UtcNow, null, r, c, s, views, null, null, null);

    [Fact]
    public void Engagement_is_known_only_when_reactions_comments_and_shares_are_all_known()
    {
        Assert.Equal(15, Fig(1, 12, 3, 0).Engagements);
        Assert.Null(Fig(2, 12, null, 0).Engagements);          // a missing comments figure is not counted as zero
        var q = PlanLogic.Quality([Fig(1, 12, 3, 0), Fig(2, 12, null, 0), new SlotFigures(3, "x", null, null, null, null, null, null, null, null, null)]);
        Assert.Equal(new DataQuality(3, 2, 1, 0), q);
    }

    [Fact]
    public void A_review_may_only_quote_figures_it_was_given_and_must_admit_low_confidence_on_thin_data()
    {
        var figs = new List<SlotFigures> { Fig(1, 12, 3, 0, 900), Fig(2, 4, 0, 0) };
        var q = PlanLogic.Quality(figs);
        ReviewResult R(string headline, string conf = "low") => new() { Headline = headline, Confidence = conf, Learnings = ["Post 1 had 12 reactions."] };

        Assert.Empty(PlanLogic.ValidateReview(R("Post 1 led with 15 engagements and 900 views"), figs, q));       // given (15 is the sum, 900 is views)
        Assert.Contains(PlanLogic.ValidateReview(R("Post 1 reached 5,000 people"), figs, q), v => v.Contains("5,000"));   // invented
        Assert.Contains(PlanLogic.ValidateReview(R("Engagement up 40%"), figs, q), v => v.Contains("40%"));              // a percentage is a computation
        Assert.Contains(PlanLogic.ValidateReview(R("Clear winner", "high"), figs, q), v => v.Contains("fewer than 5"));   // thin data cannot be high confidence
    }

    [Fact]
    public void With_nothing_to_analyse_the_review_says_so_instead_of_inventing_results()
    {
        var none = PlanLogic.NotEnoughData(new DataQuality(7, 0, 0, 0));
        Assert.Equal("low", none.Confidence); Assert.Contains("None of this plan's posts", none.Caveats[0]); Assert.Empty(none.WhatWorked);
        var waiting = PlanLogic.NotEnoughData(new DataQuality(7, 2, 0, 0));
        Assert.Contains("2 post(s) are published", waiting.Caveats[0]);
    }

    [Fact]
    public void The_review_prompt_marks_missing_figures_as_not_reported_never_zero()
    {
        var plan = new GrowthPlan { Name = "P", Goal = "G", Channel = "Facebook", Days = 7, StartDate = Today };
        var figs = new List<SlotFigures> { Fig(1, 12, 3, 0), Fig(2, null, null, null) };
        var prompt = PlanLogic.BuildReviewUser(plan, [], figs, PlanLogic.Quality(figs));
        Assert.Contains("1 | Angle 1 |", prompt); Assert.Contains("| 12 | 3 | 0 | 15 |", prompt);
        Assert.Contains("not reported | not reported | not reported | not reported", prompt);
    }
}
