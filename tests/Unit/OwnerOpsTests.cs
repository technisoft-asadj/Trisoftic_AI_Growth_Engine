using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class OwnerOpsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly AiSpend Cheap = new(1m, 20m, 3m, 60m, 0.8m);
    private static ChannelHealth Ok(DateTimeOffset? exp = null) => new("Facebook", "Ludo Fight", "Connected", exp, null);

    private static OpsSnapshot Snap(bool on = true, DateTimeOffset? lastRun = null, ChannelHealth[]? accounts = null, AiSpend? spend = null, int needs = 0, int failed = 0, DateTimeOffset? lastPost = null) =>
        new(Now, on, "Facebook", lastRun ?? Now.AddMinutes(-2), TimeSpan.FromMinutes(5), accounts ?? [Ok()], spend ?? Cheap, needs, failed, lastPost ?? Now.AddHours(-5));

    private static string[] Keys(OpsSnapshot s) => [.. OwnerAlerts.Evaluate(s).Select(a => a.Key)];

    [Fact] public void A_healthy_autopilot_raises_nothing() => Assert.Empty(OwnerAlerts.Evaluate(Snap()));
    [Fact] public void Nothing_is_raised_while_the_autopilot_is_off() => Assert.Empty(OwnerAlerts.Evaluate(Snap(on: false, lastRun: Now.AddDays(-9), needs: 3)));

    [Fact]
    public void A_token_close_to_expiry_warns_and_an_expired_one_is_critical()
    {
        Assert.Equal("warning", OwnerAlerts.Evaluate(Snap(accounts: [Ok(Now.AddDays(5))])).Single().Severity);
        Assert.Empty(OwnerAlerts.Evaluate(Snap(accounts: [Ok(Now.AddDays(30))])));
        Assert.Equal("critical", OwnerAlerts.Evaluate(Snap(accounts: [Ok(Now.AddMinutes(-1))])).Single().Severity);
    }

    [Fact]
    public void An_account_that_needs_reconnecting_is_critical_and_shows_the_reason()
    {
        var a = OwnerAlerts.Evaluate(Snap(accounts: [new("Facebook", "Ludo Fight", "NeedsReconnect", null, "Error validating access token")])).Single();
        Assert.Equal("critical", a.Severity); Assert.Contains("Error validating access token", a.Message);
    }

    [Fact] public void No_account_for_the_channel_is_critical() => Assert.Equal("no-account", OwnerAlerts.Evaluate(Snap(accounts: [new("Instagram", "Other", "Connected", null, null)])).Single().Key);

    [Fact]
    public void Budget_alerts_start_at_the_threshold_and_turn_critical_when_used_up()
    {
        Assert.Equal("warning", OwnerAlerts.Evaluate(Snap(spend: new(16m, 20m, 17m, 60m, 0.8m))).Single().Severity);
        Assert.Equal("critical", OwnerAlerts.Evaluate(Snap(spend: new(20m, 20m, 21m, 60m, 0.8m))).Single().Severity);
        Assert.Equal(["ai-budget-global"], Keys(Snap(spend: new(2m, 20m, 61m, 60m, 0.8m))));
    }

    [Fact]
    public void A_stalled_autopilot_is_noticed_only_after_several_missed_runs()
    {
        Assert.Empty(OwnerAlerts.Evaluate(Snap(lastRun: Now.AddMinutes(-15))));
        Assert.Equal(["autopilot-stalled"], Keys(Snap(lastRun: Now.AddMinutes(-40))));
    }

    [Fact]
    public void Waiting_and_failed_posts_and_a_long_silence_warn()
    {
        Assert.Equal(["needs-you"], Keys(Snap(needs: 2)));
        Assert.Equal(["failed-posts"], Keys(Snap(failed: 1)));
        Assert.Equal(["no-posts"], Keys(Snap(lastPost: Now.AddDays(-4))));
        Assert.Empty(OwnerAlerts.Evaluate(Snap(lastPost: Now.AddDays(-2))));
    }

    [Fact]
    public void The_digest_shows_what_the_network_reported_and_says_not_reported_for_the_rest()
    {
        var posts = new[]
        {
            new DigestPost("Offline pharmacy POS", Now.AddDays(-3), 8, 2, 1, null, null, null),
            new DigestPost("Closing time rush", Now.AddDays(-1), null, null, null, null, null, null),
        };
        var text = DigestWriter.Write(new(Now.AddDays(-7), Now, posts, 5, 2, 1, 0.42m, ["Question openings draw more comments"], [], 3, 1, ["The connection to Ludo Fight expires on 2026-11-25. Reconnect it before then."], 1.5m, 20m));
        Assert.Contains("2 post(s) went out; 1 have figures", text);
        Assert.Contains("8 reactions, 2 comments, 1 shares", text);
        Assert.Contains("reactions not reported, comments not reported, shares not reported", text);   // never "0"
        Assert.DoesNotContain("0 reactions", text);
        Assert.Contains("5 approved, 2 sent back for a rewrite, 1 left for you", text);
        Assert.Contains("Supported: Question openings draw more comments", text);
        Assert.Contains("Waiting for you: 1 post(s)", text);
        Assert.Contains("expires on 2026-11-25", text);
        Assert.Contains("$1.50 of $20.00", text);
    }

    [Fact]
    public void A_quiet_week_still_produces_an_honest_digest()
    {
        var text = DigestWriter.Write(new(Now.AddDays(-7), Now, [], 0, 0, 0, 0m, [], [], 0, 0, [], 0m, 20m));
        Assert.Contains("No posts went out this week.", text); Assert.Contains("nothing to check", text); Assert.Contains("nothing was settled", text); Assert.Contains("Open problems: none.", text);
    }

    [Fact]
    public void Only_posts_with_figures_can_be_called_the_strongest_and_a_single_one_is_not_a_comparison()
    {
        var one = new[] { new DigestPost("Only one", Now.AddDays(-1), 4, 0, 0, null, null, null), new DigestPost("No figures", Now.AddDays(-2), null, null, null, null, null, null) };
        Assert.DoesNotContain("Strongest", DigestWriter.Write(new(Now.AddDays(-7), Now, one, 0, 0, 0, 0m, [], [], 0, 0, [], 0m, 20m)));
        var two = new[] { new DigestPost("A", Now.AddDays(-1), 4, 0, 0, null, null, null), new DigestPost("B", Now.AddDays(-2), 9, 1, 0, null, null, null) };
        Assert.Contains("Strongest: \"B\" with 10", DigestWriter.Write(new(Now.AddDays(-7), Now, two, 0, 0, 0, 0m, [], [], 0, 0, [], 0m, 20m)));
    }

    [Fact]
    public void The_digest_reports_link_clicks_as_clicks_and_never_as_installs()
    {
        var posts = new[] { new DigestPost("Offline pharmacy POS", Now.AddDays(-3), 8, 2, 1, null, null, null, LinkClicks: 14), new DigestPost("Closing time rush", Now.AddDays(-1), null, null, null, null, null, null, LinkClicks: 0) };
        var text = DigestWriter.Write(new(Now.AddDays(-7), Now, posts, 0, 0, 0, 0m, [], [], 0, 0, [], 0m, 20m, LinkClicks: 14, LinkVisitors: 11));
        Assert.Contains("14 link click(s)", text); Assert.Contains("0 link click(s)", text);
        Assert.Contains("Link clicks this week: 14 (about 11 different visitors)", text);
        Assert.Contains("installs cannot be measured", text);
        Assert.DoesNotContain("installs:", text);
        var none = DigestWriter.Write(new(Now.AddDays(-7), Now, [], 0, 0, 0, 0m, [], [], 0, 0, [], 0m, 20m));
        Assert.DoesNotContain("Link clicks", none);   // no links, no line
    }
}
