using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class LeaderboardTests
{
    private static LeaderboardRow Row(string format = "Post", string? hook = "Question", int? eng = 10, long? views = null, string at = "2026-11-16T13:00:00Z")
        => new(Guid.NewGuid(), format, hook, DateTimeOffset.Parse(at), eng, views);

    private static LeaderboardCell Cell(LeaderboardResult r, string dim, string key) => r.Dimensions.Single(d => d.Name == dim).Cells.Single(c => c.Key == key);

    [Fact]
    public void A_cell_with_fewer_than_five_measured_posts_is_labelled_too_few()
    {
        var r = Leaderboard.Build("Facebook", 0, [.. Enumerable.Range(0, 4).Select(_ => Row())]);
        Assert.True(Cell(r, "Hook type", "Question").TooFew);
        var five = Leaderboard.Build("Facebook", 0, [.. Enumerable.Range(0, 5).Select(_ => Row())]);
        Assert.False(Cell(five, "Hook type", "Question").TooFew);
    }

    [Fact]
    public void Unreported_figures_are_not_zero_and_do_not_count_as_measured()
    {
        var r = Leaderboard.Build("Facebook", 0, [Row(eng: null), Row(eng: null), Row(eng: 20)]);
        var c = Cell(r, "Format", "Post");
        Assert.Equal(3, c.Posts); Assert.Equal(1, c.Measured);
        Assert.Equal(20m, c.AvgEngagements);        // the two unreported posts are left out, not averaged in as zero
        Assert.Null(c.AvgViews); Assert.Equal(0, c.WithViews);
        Assert.Equal(1, r.Measured);
        Assert.Null(Cell(Leaderboard.Build("Facebook", 0, [Row(eng: null)]), "Format", "Post").AvgEngagements);
    }

    [Fact]
    public void A_thin_cell_never_outranks_a_solid_one()
    {
        var rows = new List<LeaderboardRow>();
        rows.AddRange(Enumerable.Range(0, 5).Select(_ => Row(hook: "Tip", eng: 10)));
        rows.Add(Row(hook: "Challenge", eng: 500));   // one lucky post
        var cells = Leaderboard.Build("Facebook", 0, rows).Dimensions.Single(d => d.Name == "Hook type").Cells;
        Assert.Equal("Tip", cells[0].Key); Assert.False(cells[0].TooFew);
        Assert.Equal("Challenge", cells[1].Key); Assert.True(cells[1].TooFew);
    }

    [Fact]
    public void Untagged_posts_are_grouped_and_say_so()
    {
        var r = Leaderboard.Build("Facebook", 0, [Row(hook: null)]);
        Assert.Equal(1, Cell(r, "Hook type", Leaderboard.Untagged).Posts);
    }

    [Fact]
    public void Weekday_and_time_slot_follow_the_configured_offset()
    {
        // Monday 22:00 UTC is Tuesday 03:00 at UTC+5
        var utc = Leaderboard.Build("Facebook", 0, [Row(at: "2026-11-16T22:00:00Z")]);
        Assert.Equal(1, Cell(utc, "Weekday", "Monday").Posts); Assert.Equal(1, Cell(utc, "Time slot", "Evening 19-23").Posts);
        var pk = Leaderboard.Build("Facebook", 5, [Row(at: "2026-11-16T22:00:00Z")]);
        Assert.Equal(1, Cell(pk, "Weekday", "Tuesday").Posts); Assert.Equal(1, Cell(pk, "Time slot", "Night 23-05").Posts);
        Assert.Equal("UTC+5", pk.TimeZoneLabel);
    }

    [Fact]
    public void Engagements_need_all_three_figures()
    {
        PostPerformance P(int? r, int? c, int? s) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, null, null, r, c, s, null, null, null, null);
        Assert.Equal(15, Leaderboard.Engagements(P(12, 3, 0)));
        Assert.Null(Leaderboard.Engagements(P(12, null, 0)));
        Assert.Null(Leaderboard.Engagements(null));
    }

    [Fact]
    public void Only_figures_from_the_table_the_profile_or_small_counts_may_be_quoted()
    {
        var table = Leaderboard.Build("Facebook", 0, [.. Enumerable.Range(0, 5).Select(_ => Row(eng: 15, views: 320))]);
        Assert.Empty(Leaderboard.FiguresNotInTable(["Tip posts averaged 15 engagements and 320 views", "Post 3 of 7", "Up to 4 players"], table, ["Play with up to 4 friends"]));
        Assert.Contains("99", Leaderboard.FiguresNotInTable(["Tips averaged 99 engagements"], table));
        Assert.Contains("40%", Leaderboard.FiguresNotInTable(["a 40% lift"], table));
        Assert.Contains("500", Leaderboard.FiguresNotInTable(["about 500 views"], null));      // no table at all: no figure may be quoted
        Assert.Contains("2026", Leaderboard.FiguresNotInTable(["since 2026"], table));
        Assert.Empty(Leaderboard.FiguresNotInTable(["since 2026"], table, ["Launched in 2026"]));   // a number the verified profile states is a fact
    }

    [Fact]
    public void A_plan_with_an_unknown_hook_type_or_an_invented_figure_is_refused()
    {
        PlanDraft Draft(string hook, string rationale) => new() { Rationale = rationale, Measures = [new Measure { Name = "Reactions", Why = "interest" }], Slots = [new PlannedPost { Angle = "One", Brief = "Cover a fact.", HookType = hook }] };
        Assert.Empty(PlanLogic.ValidatePlan(Draft("Question", "A mix."), 1));
        Assert.Contains(PlanLogic.ValidatePlan(Draft("Clickbait", "A mix."), 1), v => v.Contains("hookType"));
        Assert.Contains(PlanLogic.ValidatePlan(Draft("Question", "Questions averaged 99 engagements."), 1), v => v.Contains("99"));
        Assert.Empty(PlanLogic.ValidatePlan(new PlanDraft { Rationale = "A mix.", Measures = [new Measure { Name = "x", Why = "y" }], Slots = [new PlannedPost { Angle = "One", Brief = "Cover a fact." }] }, 1));   // a missing hook type is allowed
    }
}
