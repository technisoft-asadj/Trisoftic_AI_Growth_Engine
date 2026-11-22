using TriSoftic.GrowthEngine.Modules.Links;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class TrackedLinkTests
{
    private const string Play = "https://play.google.com/store/apps/details?id=com.devorbit.ludofight";

    [Fact]
    public void A_Play_Store_page_gets_the_referrer_parameter_that_carries_the_tags()
    {
        var url = UrlTracking.Build(Play, "Facebook", "Ludo Fight", "Ab3dE9x");
        Assert.StartsWith(Play + "&referrer=", url);
        var referrer = Uri.UnescapeDataString(url[(url.IndexOf("referrer=", StringComparison.Ordinal) + 9)..]);
        Assert.Equal("utm_source=facebook&utm_medium=social&utm_campaign=ludo-fight&utm_content=Ab3dE9x", referrer);
        Assert.DoesNotContain("utm_source=facebook&", url.Replace(Uri.EscapeDataString("utm_source=facebook"), ""));   // the tags sit inside the referrer, not beside it
    }

    [Fact]
    public void Other_addresses_get_plain_utm_tags_and_keep_their_own_query_and_fragment()
    {
        var url = UrlTracking.Build("https://example.com/pos?plan=lifetime#pricing", "facebook", "Trisoftic POS", "Zz9");
        Assert.Equal("https://example.com/pos?plan=lifetime&utm_source=facebook&utm_medium=social&utm_campaign=trisoftic-pos&utm_content=Zz9#pricing", url);
        Assert.Equal("https://example.com/?utm_source=facebook&utm_medium=social&utm_campaign=x&utm_content=c", UrlTracking.Build("https://example.com/", "facebook", "x", "c"));
    }

    [Fact]
    public void Tags_already_on_the_address_are_never_overwritten()
    {
        var own = UrlTracking.Build("https://example.com/?utm_source=newsletter&utm_campaign=spring", "facebook", "x", "c");
        Assert.Contains("utm_source=newsletter", own); Assert.DoesNotContain("utm_source=facebook", own); Assert.Contains("utm_campaign=spring", own);
        Assert.Contains("utm_medium=social", own);
        var play = UrlTracking.Build(Play + "&referrer=utm_source%3Dcampaign1", "facebook", "x", "c");
        Assert.Equal(Play + "&referrer=utm_source%3Dcampaign1", play);
    }

    [Theory]
    [InlineData("Ludo Fight", "ludo-fight")]
    [InlineData("  Trisoftic POS!! ", "trisoftic-pos")]
    [InlineData("", "")]
    public void Names_become_safe_tags(string name, string slug) => Assert.Equal(slug, UrlTracking.Slug(name));

    [Theory]
    [InlineData("https://example.com/x", true)]
    [InlineData("http://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pw@example.com/", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void Only_web_addresses_can_be_tracked(string url, bool ok) => Assert.Equal(ok, UrlTracking.IsWebAddress(url));

    [Theory]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)", true)]
    [InlineData("Facebot", true)]
    [InlineData("WhatsApp/2.23.20 A", true)]
    [InlineData("Twitterbot/1.0", true)]
    [InlineData("curl/8.4.0", true)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1)", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-A135F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36", false)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1", false)]
    public void Link_previews_and_crawlers_are_not_clicks(string? ua, bool robot) => Assert.Equal(robot, VisitorRules.LooksLikeRobot(ua));

    [Fact]
    public void The_visitor_hash_is_stable_within_a_day_and_changes_the_next_day_and_for_other_people()
    {
        var noon = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var a = VisitorRules.Hash("secret", noon, "1.2.3.4", "Chrome");
        Assert.Equal(a, VisitorRules.Hash("secret", noon.AddHours(5), "1.2.3.4", "Chrome"));
        Assert.NotEqual(a, VisitorRules.Hash("secret", noon.AddDays(1), "1.2.3.4", "Chrome"));
        Assert.NotEqual(a, VisitorRules.Hash("secret", noon, "5.6.7.8", "Chrome"));
        Assert.NotEqual(a, VisitorRules.Hash("other", noon, "1.2.3.4", "Chrome"));
        Assert.Equal(16, a.Length); Assert.DoesNotContain("1.2.3.4", a);
    }
}
