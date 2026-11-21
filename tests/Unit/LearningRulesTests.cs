using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class LearningRulesTests
{
    private static readonly Guid H1 = Guid.NewGuid(), H2 = Guid.NewGuid(), H3 = Guid.NewGuid(), H4 = Guid.NewGuid();

    private static PlanDraft Draft(params string?[] tests) => new()
    {
        Rationale = "A mix.", Measures = [new Measure { Name = "Reactions", Why = "interest" }],
        Slots = [.. tests.Select((t, i) => new PlannedPost { Angle = "Angle " + i, Brief = "Cover a fact.", HookType = "Question", Tests = t })],
    };

    [Fact]
    public void A_plan_must_test_the_open_hypotheses_up_to_three_and_only_real_ids()
    {
        var open = new[] { H1, H2, H3, H4 };
        Assert.Empty(PlanLogic.ValidatePlan(Draft(H1.ToString(), H2.ToString(), H3.ToString(), null), 4, null, null, null, null, open));
        Assert.Contains(PlanLogic.ValidatePlan(Draft(H1.ToString(), null, null, null), 4, null, null, null, null, open), v => v.Contains("at least 3"));
        Assert.Contains(PlanLogic.ValidatePlan(Draft(H1.ToString(), H1.ToString(), H1.ToString(), null), 4, null, null, null, null, open), v => v.Contains("at least 3"));   // the same one three times is one
        Assert.Contains(PlanLogic.ValidatePlan(Draft(Guid.NewGuid().ToString(), H1.ToString(), H2.ToString(), H3.ToString()), 4, null, null, null, null, open), v => v.Contains("open hypotheses"));
        Assert.Empty(PlanLogic.ValidatePlan(Draft(H1.ToString()), 1, null, null, null, null, [H1, H2]));   // a one-post plan needs one
        Assert.Empty(PlanLogic.ValidatePlan(Draft(null, null), 2, null, null, null, null, []));            // nothing open: nothing required
    }

    private static ReviewResult Review(string confidence = "low", params HypothesisUpdate[] updates) => new()
    {
        Headline = "A headline", Confidence = confidence, Updates = [.. updates],
        Hypotheses = [new NewHypothesis { Statement = "Questions draw more comments", HowToTest = "Alternate them" }],
    };
    private static HypothesisUpdate U(Guid id, string outcome) => new() { Id = id, Outcome = outcome, Evidence = "Evidence given." };
    private static readonly IReadOnlyList<SlotFigures> None = [];

    [Fact]
    public void A_review_may_only_judge_open_hypotheses_and_only_confidently_with_enough_measured_posts()
    {
        var q5 = new DataQuality(5, 5, 5, 0); var q2 = new DataQuality(2, 2, 2, 0);
        Assert.Empty(PlanLogic.ValidateReview(Review("medium", U(H1, "supported")), None, q5, [H1]));
        Assert.Contains(PlanLogic.ValidateReview(Review("low", U(H1, "supported")), None, q2, [H1]), v => v.Contains("inconclusive"));
        Assert.Empty(PlanLogic.ValidateReview(Review("low", U(H1, "inconclusive")), None, q2, [H1]));
        Assert.Contains(PlanLogic.ValidateReview(Review("low", U(H2, "inconclusive")), None, q2, [H1]), v => v.Contains("id of an open hypothesis"));
        Assert.Contains(PlanLogic.ValidateReview(Review("low", U(H1, "maybe")), None, q2, [H1]), v => v.Contains("outcome"));
        Assert.Contains(PlanLogic.ValidateReview(Review("low", U(H1, "inconclusive"), U(H1, "inconclusive")), None, q2, [H1]), v => v.Contains("one update"));
    }

    [Fact]
    public void New_hypotheses_are_limited_and_cannot_hide_invented_figures()
    {
        var q = new DataQuality(2, 2, 2, 0);
        var many = Review(); many.Hypotheses = [.. Enumerable.Range(0, 4).Select(i => new NewHypothesis { Statement = "Idea " + i, HowToTest = "x" })];
        Assert.Contains(PlanLogic.ValidateReview(many, None, q, []), v => v.Contains("at most 3"));
        var invented = Review(); invented.Hypotheses = [new NewHypothesis { Statement = "Videos get 4,000 views", HowToTest = "x" }];
        Assert.Contains(PlanLogic.ValidateReview(invented, None, q, []), v => v.Contains("4,000"));
    }
}
