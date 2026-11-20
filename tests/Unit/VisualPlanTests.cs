using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class VisualPlanTests
{
    private static PlanDraft Draft(params (string? visual, string? brief)[] slots) => new()
    {
        Rationale = "A mix.", Measures = [new Measure { Name = "Reactions", Why = "interest" }],
        Slots = [.. slots.Select((s, i) => new PlannedPost { Angle = "Angle " + i, Brief = "Cover a fact.", HookType = "Question", Visual = s.visual, ImageBrief = s.brief })],
    };
    private static string? Brief(string b) => b.Contains("friends") ? "People are not allowed." : null;

    [Fact]
    public void Without_visual_options_the_visual_fields_are_ignored()
        => Assert.Empty(PlanLogic.ValidatePlan(Draft((null, null)), 1));

    [Fact]
    public void With_visual_options_every_slot_needs_text_image_or_video()
    {
        var o = new VisualOptions(true, 1);
        Assert.Empty(PlanLogic.ValidatePlan(Draft(("text", null), ("video", null), ("image", "A dice on a board")), 3, null, null, o, Brief));
        Assert.Contains(PlanLogic.ValidatePlan(Draft((null, null)), 1, null, null, o, Brief), v => v.Contains("visual"));
        Assert.Contains(PlanLogic.ValidatePlan(Draft(("gif", null)), 1, null, null, o, Brief), v => v.Contains("visual"));
    }

    [Fact]
    public void Video_cannot_be_used_more_than_the_uploaded_videos_available()
    {
        Assert.Contains(PlanLogic.ValidatePlan(Draft(("video", null), ("video", null)), 2, null, null, new VisualOptions(true, 1), Brief), v => v.Contains("only 1 uploaded video"));
        Assert.Contains(PlanLogic.ValidatePlan(Draft(("video", null)), 1, null, null, new VisualOptions(true, 0), Brief), v => v.Contains("only 0 uploaded video"));
    }

    [Fact]
    public void An_image_needs_a_safe_brief_and_images_can_be_switched_off()
    {
        var o = new VisualOptions(true, 0);
        Assert.Contains(PlanLogic.ValidatePlan(Draft(("image", null)), 1, null, null, o, Brief), v => v.Contains("imageBrief"));
        Assert.Contains(PlanLogic.ValidatePlan(Draft(("image", "friends around a table")), 1, null, null, o, Brief), v => v.Contains("not allowed"));
        Assert.Contains(PlanLogic.ValidatePlan(Draft(("image", "A dice on a board")), 1, null, null, new VisualOptions(false, 0), Brief), v => v.Contains("switched off"));
    }

    private static AssetInfo Asset(bool synthetic, string rights = "Unrestricted", string type = "image/png") => new(Guid.NewGuid(), Guid.Empty, "file", type, 10, false, rights, null, synthetic);

    [Fact]
    public void An_ai_picture_must_not_be_called_a_screenshot_and_uploaded_files_need_clear_rights()
    {
        string Failed(IEnumerable<GateResult> g) => string.Join("|", g.Where(x => !x.Passed).Select(x => x.Name));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("", Failed(AutopilotGates.CheckMedia("Post", [Asset(true)], Guid.Empty, now, "Keep selling offline.", "A dice on a board", Brief)));
        Assert.Contains("illustration not passed off as real", Failed(AutopilotGates.CheckMedia("Post", [Asset(true)], Guid.Empty, now, "Look at this real gameplay screenshot", null, Brief)));
        Assert.Contains("media rights are clear", Failed(AutopilotGates.CheckMedia("Post", [Asset(false, "Unknown")], Guid.Empty, now, "Hello", null, Brief)));
        Assert.Contains("picture brief is safe", Failed(AutopilotGates.CheckMedia("Post", [Asset(true)], Guid.Empty, now, "Hello", "friends laughing", Brief)));
        Assert.Empty(AutopilotGates.CheckMedia("Post", [], Guid.Empty, now, "Text only", null, Brief));
    }

    [Fact]
    public void The_leaderboard_compares_text_only_posts_with_own_pictures_ai_pictures_and_video()
    {
        LeaderboardRow R(string visual, int eng) => new(Guid.NewGuid(), "Post", "Question", DateTimeOffset.Parse("2026-11-16T13:00:00Z"), eng, null, visual);
        var r = Leaderboard.Build("Facebook", 0, [R("Text only", 4), R("AI picture", 9), R("AI picture", 11), R("Video", 20)]);
        var cells = r.Dimensions.Single(d => d.Name == "Visual").Cells;
        Assert.Equal(3, cells.Count);
        Assert.Equal(10m, cells.Single(c => c.Key == "AI picture").AvgEngagements);
        Assert.All(cells, c => Assert.True(c.TooFew));
    }
}
