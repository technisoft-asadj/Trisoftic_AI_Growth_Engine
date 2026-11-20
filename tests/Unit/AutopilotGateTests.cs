using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class AutopilotGateTests
{
    private const string Store = "https://play.google.com/store/apps/details?id=com.devorbit.ludofight";
    private static readonly ApprovedKnowledge K = new(Guid.NewGuid(), "Ludo Fight", "Game", "Live", 1, "Classic Ludo for Android",
        [new KnowledgeFact("f1", "Feature", "Play with up to 4 friends online"), new KnowledgeFact("f2", "Store", "Get it on Google Play: " + Store)],
        [new KnowledgeClaim("c1", "Free to play with ads", false), new KnowledgeClaim("c2", "Number one ludo game in the world", true)],
        [], [new KnowledgeMarket("PK", ["en"])], ["Android"], "friendly", ["be warm"], ["no hype"]);

    private static List<GateResult> Check(string body, params string[] recent) => AutopilotGates.Check(body, K, recent);
    private static string Failed(List<GateResult> g) => string.Join("|", g.Where(x => !x.Passed).Select(x => x.Name));

    [Fact]
    public void A_plain_grounded_post_with_the_stores_own_link_passes_every_check()
        => Assert.Equal("", Failed(Check("Roll the dice and play with up to 4 friends online. Free to play. " + Store)));

    [Theory]
    [InlineData("Like and share if you love ludo!", "no engagement bait")]
    [InlineData("Comment 1 to win a prize", "no engagement bait")]
    [InlineData("Tag a friend who always loses", "no engagement bait")]
    [InlineData("Follow to unlock a surprise", "no engagement bait")]
    [InlineData("Trusted by 5,000 players", "figures come from the profile")]
    [InlineData("Rated 4.8 stars", "figures come from the profile")]
    [InlineData("Up to 40% more fun", "figures come from the profile")]
    [InlineData("Install now: https://evil.example/ludo", "links are the product's own")]
    [InlineData("Our game is the number one ludo game in the world", "no prohibited claim")]
    [InlineData("Guaranteed fun every time", "no guarantee or superlative")]
    [InlineData("Thanks @someone for playing", "no tagging of people")]
    [InlineData("Fill in [store link] here", "no placeholder")]
    [InlineData("", "has text")]
    public void Each_rule_rejects_what_it_is_for(string body, string rule) => Assert.Contains(rule, Failed(Check(body)).Split('|'));

    [Fact]
    public void A_figure_the_profile_states_is_allowed_and_a_links_digits_are_not_counted_as_figures()
        => Assert.Equal("", Failed(Check("Up to 4 friends can join. " + Store)));

    [Fact]
    public void A_near_copy_of_a_recent_post_is_a_repeat_but_a_different_post_is_not()
    {
        const string old = "Roll the dice and play with friends online today";
        Assert.Contains("not a repeat", Failed(Check("Roll the dice and play with friends online today!", old)).Split('|'));
        Assert.DoesNotContain("not a repeat", Failed(Check("Pass and play on one phone when the internet is slow", old)).Split('|'));
    }

    [Fact]
    public void Shouting_and_hashtag_stuffing_are_refused()
    {
        Assert.Contains("not shouting", Failed(Check("PLAY LUDO NOW WITH YOUR FRIENDS ONLINE")).Split('|'));
        Assert.Contains("not shouting", Failed(Check("Fun #a #b #c #d #e #f")).Split('|'));
    }

    private static ReviewerVerdict V(string verdict, params (string sev, string text)[] issues) => new() { Verdict = verdict, Summary = "summary", Issues = [.. issues.Select(i => new ReviewerIssue { Severity = i.sev, Text = i.text })] };
    private static readonly List<GateResult> Pass = [new("x", true, "ok")];
    private static readonly List<GateResult> Fail = [new("x", false, "Broke a rule.")];

    [Fact]
    public void A_failed_check_rejects_whatever_the_reviewer_says()
    {
        var (o, why) = AutopilotReviewer.Decide(Fail, V("approve"));
        Assert.Equal(AutopilotOutcome.Reject, o); Assert.Contains("Broke a rule", why);
    }

    [Fact]
    public void The_reviewer_can_approve_only_a_post_that_passed_every_check_and_listed_no_blocker()
    {
        Assert.Equal(AutopilotOutcome.Approve, AutopilotReviewer.Decide(Pass, V("approve")).Outcome);
        Assert.Equal(AutopilotOutcome.Approve, AutopilotReviewer.Decide(Pass, V("approve", ("minor", "a bit flat"))).Outcome);
        Assert.Equal(AutopilotOutcome.Reject, AutopilotReviewer.Decide(Pass, V("approve", ("blocker", "claims a feature not in the facts"))).Outcome);   // says approve, but lists a blocker
        Assert.Equal(AutopilotOutcome.Reject, AutopilotReviewer.Decide(Pass, V("reject")).Outcome);
        Assert.Equal(AutopilotOutcome.Escalate, AutopilotReviewer.Decide(Pass, V("escalate")).Outcome);
        Assert.Equal(AutopilotOutcome.Escalate, AutopilotReviewer.Decide(Pass, null).Outcome);   // no usable answer: a person decides, never an approval by default
    }

    [Fact]
    public void The_reviewers_answer_must_be_a_known_verdict()
    {
        Assert.Null(AutopilotReviewer.Parse("""{"verdict":"maybe","issues":[]}""", out var e)); Assert.NotNull(e);
        Assert.Null(AutopilotReviewer.Parse("not json", out _));
        Assert.Equal("approve", AutopilotReviewer.Parse("""Here: {"verdict":"Approve","issues":[],"summary":"fine"}""", out _)!.Verdict);
    }
}
