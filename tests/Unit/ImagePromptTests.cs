using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Ai;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class ImagePromptTests
{
    [Theory]
    [InlineData("Friends laughing around a colourful board on a sunny balcony")]
    [InlineData("A family playing dice at a kitchen table, warm evening light")]
    [InlineData("An apple tree and a red kite over green hills")]
    [InlineData("A cheerful cartoon rocket flying over a city skyline")]
    public void Ordinary_scene_descriptions_are_allowed(string brief) => Assert.Null(ImagePrompt.Refusal(brief));

    [Theory]
    [InlineData("a screenshot of the home screen", "screenshot")]
    [InlineData("the app UI on a phone", "interface")]
    [InlineData("gameplay of a ludo match", "gameplay")]
    [InlineData("a banner with the slogan Play Now", "text")]
    [InlineData("our logo in gold", "logo")]
    [InlineData("a Facebook like button", "brand")]
    [InlineData("a celebrity holding a trophy", "person")]
    public void A_brief_for_something_an_illustration_must_not_be_is_refused_with_a_reason(string brief, string reasonContains)
    {
        var reason = ImagePrompt.Refusal(brief);
        Assert.NotNull(reason);
        Assert.StartsWith("This brief was not sent", reason);
        Assert.Contains(reasonContains, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_prompt_carries_the_product_summary_and_the_fixed_safety_rules_and_nothing_unapproved()
    {
        var k = new ApprovedKnowledge(Guid.NewGuid(), "Ludo Fight", "MobileGame", "Live", 3, "A multiplayer ludo game for Android", [], [], [], [], [], "playful", [], []);
        var p = ImagePrompt.Build(k, "Friends cheering. ");
        Assert.StartsWith("Friends cheering. ", p);
        Assert.Contains("Ludo Fight", p);
        Assert.Contains("multiplayer ludo game", p);
        Assert.Contains("Mood: playful", p);
        Assert.Contains("no text, no letters, no logos", p);
        Assert.Contains("user interface", ImagePrompt.Negative);
    }
}
