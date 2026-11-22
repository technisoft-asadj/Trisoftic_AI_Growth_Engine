using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class PostsPerDayTests
{
    private static readonly DateOnly Start = new(2026, 11, 23);
    private static PlanSpec Spec(int days, int every, string time, params string[] more) =>
        new("Plan", "Goal", "Facebook", null, Start, days, every, TimeOnly.Parse(time), more.Length == 0 ? null : more.Select(TimeOnly.Parse).ToList());

    [Fact]
    public void One_time_keeps_the_old_layout_of_one_post_per_posting_day()
    {
        var t = PlanLogic.SlotTimes(Spec(7, 3, "13:00"));
        Assert.Equal(3, t.Count);
        Assert.All(t, x => Assert.Equal(13, x.Hour));
        Assert.Equal(3, PlanLogic.SlotCount(7, 3));
    }

    [Fact]
    public void Three_times_give_three_posts_on_each_posting_day_in_time_order()
    {
        var spec = Spec(3, 1, "13:00", "19:00", "08:00");
        var t = PlanLogic.SlotTimes(spec);
        Assert.Equal(9, t.Count); Assert.Equal(9, PlanLogic.SlotCount(3, 1, PlanLogic.PerDay(spec)));
        Assert.Equal([8, 13, 19, 8, 13, 19, 8, 13, 19], t.Select(x => x.Hour));
        Assert.Equal(t.OrderBy(x => x), t);
        Assert.Equal([23, 23, 23, 24, 24, 24, 25, 25, 25], t.Select(x => x.Day));
    }

    [Fact]
    public void Posting_days_apart_still_apply_with_several_posts_per_day()
    {
        var t = PlanLogic.SlotTimes(Spec(7, 3, "08:00", "20:00"));
        Assert.Equal(6, t.Count);
        Assert.Equal([23, 23, 26, 26, 29, 29], t.Select(x => x.Day));
    }

    [Fact]
    public void A_repeated_time_does_not_create_a_second_post()
    {
        var spec = Spec(3, 1, "13:00", "13:00"); Assert.Equal(3, PlanLogic.SlotTimes(spec).Count); Assert.Equal(1, PlanLogic.PerDay(spec));   // the duplicate collapses
    }

    [Fact]
    public void Times_must_be_one_to_three_and_at_least_two_hours_apart_including_across_midnight()
    {
        Assert.Empty(PlanLogic.ValidateTimes([TimeOnly.Parse("13:00")]));
        Assert.Empty(PlanLogic.ValidateTimes([TimeOnly.Parse("07:00"), TimeOnly.Parse("13:00"), TimeOnly.Parse("19:00")]));
        Assert.NotEmpty(PlanLogic.ValidateTimes([TimeOnly.Parse("13:00"), TimeOnly.Parse("14:30")]));
        Assert.NotEmpty(PlanLogic.ValidateTimes([TimeOnly.Parse("23:00"), TimeOnly.Parse("00:30")]));    // 90 minutes across midnight
        Assert.NotEmpty(PlanLogic.ValidateTimes([]));
        Assert.NotEmpty(PlanLogic.ValidateTimes([TimeOnly.Parse("01:00"), TimeOnly.Parse("05:00"), TimeOnly.Parse("09:00"), TimeOnly.Parse("13:00")]));
    }

    [Fact]
    public void A_plan_that_would_hold_too_many_posts_is_refused_and_the_prompt_names_the_times()
    {
        var big = Spec(14, 1, "08:00", "13:00", "19:00");   // 42 posts
        Assert.Contains(PlanLogic.Validate(big, Start), e => e.Contains("more than"));
        Assert.Empty(PlanLogic.Validate(Spec(7, 1, "08:00", "13:00", "19:00"), Start));            // 21 posts
        var user = PlanLogic.BuildPlanUser(Spec(7, 1, "08:00", "13:00", "19:00"), 21, null);
        Assert.Contains("3 posts on every posting day", user); Assert.Contains("08:00, 13:00, 19:00 UTC", user); Assert.Contains("never sees the same message twice in one day", user);
    }
}
