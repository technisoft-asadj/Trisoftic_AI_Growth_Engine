using TriSoftic.GrowthEngine.Modules.Planning;

namespace TriSoftic.GrowthEngine.Tests.Unit;

/// <summary>When a product also posts to Instagram, the planner may not choose text-only posts (Instagram would skip those days).</summary>
public sealed class InstagramPlanTests
{
    private static PlanDraft Draft(params string[] visuals) => new()
    {
        Rationale = "A mix.",
        Measures = [new Measure { Name = "reach", Why = "shows interest" }],
        Slots = [.. visuals.Select(v => new PlannedPost { Angle = "A " + v + Guid.NewGuid().ToString("N")[..4], Brief = "Cover a fact.", Visual = v, ImageBrief = v == "image" ? "A dice rolling across a colourful board." : null })],
    };

    [Fact]
    public void Text_only_slots_are_rejected_when_a_picture_is_required_and_allowed_otherwise()
    {
        var plain = new VisualOptions(true, 0);
        var needed = new VisualOptions(true, 0, RequireVisual: true);
        Assert.Empty(PlanLogic.ValidatePlan(Draft("text", "image"), 2, null, null, plain));
        Assert.Contains(PlanLogic.ValidatePlan(Draft("text", "image"), 2, null, null, needed), v => v.Contains("Instagram"));
        Assert.Empty(PlanLogic.ValidatePlan(Draft("image", "image"), 2, null, null, needed));
    }
}
