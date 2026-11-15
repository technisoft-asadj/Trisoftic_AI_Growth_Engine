using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public enum StageKind { Open, Won, Lost }
public enum OpportunityStatus { Open, Won, Lost }
public enum ActivityType { Call, Email, Meeting, Task, Note }

public sealed class Pipeline : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    /// <summary>Null = usable for every product.</summary>
    public Guid? ProductId { get; set; }
    public string Name { get; set; } = "";
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PipelineStage : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid PipelineId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    /// <summary>Default win probability (0-100) for deals in this stage.</summary>
    public int Probability { get; set; }
    public StageKind Kind { get; set; }
}

public sealed class Opportunity : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid PipelineId { get; set; }
    public Guid StageId { get; set; }
    public Guid ContactId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? CampaignId { get; set; }
    public string Name { get; set; } = "";
    public decimal Value { get; set; }
    public string Currency { get; set; } = "USD";
    public int? ProbabilityOverride { get; set; }
    public DateOnly? ExpectedCloseDate { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? Source { get; set; }
    public string? NextStep { get; set; }
    public OpportunityStatus Status { get; set; } = OpportunityStatus.Open;
    public string? WonLostReason { get; set; }
    /// <summary>Actual revenue recorded when the deal is won (may differ from the quoted Value).</summary>
    public decimal? RevenueAmount { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OpportunityStageChange : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid OpportunityId { get; set; }
    public Guid? FromStageId { get; set; }
    public Guid ToStageId { get; set; }
    public string? Note { get; set; }
    public Guid? ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class SalesActivity : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid OpportunityId { get; set; }
    public ActivityType Type { get; set; }
    public string Summary { get; set; } = "";
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Pure rules for moving a deal between stages, so they can be tested without a database.</summary>
public static class OpportunityRules
{
    public static int EffectiveProbability(Opportunity o, PipelineStage stage) =>
        stage.Kind == StageKind.Won ? 100 : stage.Kind == StageKind.Lost ? 0 : o.ProbabilityOverride ?? stage.Probability;

    public static void Move(Opportunity o, PipelineStage to, string? reason, decimal? revenue, DateTimeOffset now)
    {
        if (to.PipelineId != o.PipelineId) throw new IllegalTransitionException("The stage belongs to a different pipeline");
        if (o.Status != OpportunityStatus.Open) throw new IllegalTransitionException($"A {o.Status} deal is closed; reopen it first");
        if (to.Id == o.StageId) throw new IllegalTransitionException("The deal is already in that stage");
        switch (to.Kind)
        {
            case StageKind.Won:
                var rev = revenue ?? o.Value;
                if (rev < 0) throw new IllegalTransitionException("Revenue cannot be negative");
                o.Status = OpportunityStatus.Won; o.RevenueAmount = rev; o.WonLostReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(); o.ClosedAt = now;
                break;
            case StageKind.Lost:
                if (string.IsNullOrWhiteSpace(reason)) throw new IllegalTransitionException("A reason is required when a deal is lost");
                o.Status = OpportunityStatus.Lost; o.WonLostReason = reason.Trim(); o.RevenueAmount = null; o.ClosedAt = now;
                break;
        }
        o.StageId = to.Id; o.UpdatedAt = now;
    }

    public static void Reopen(Opportunity o, PipelineStage to, DateTimeOffset now)
    {
        if (o.Status == OpportunityStatus.Open) throw new IllegalTransitionException("The deal is already open");
        if (to.Kind != StageKind.Open || to.PipelineId != o.PipelineId) throw new IllegalTransitionException("Reopen into an open stage of the same pipeline");
        o.Status = OpportunityStatus.Open; o.StageId = to.Id; o.WonLostReason = null; o.RevenueAmount = null; o.ClosedAt = null; o.UpdatedAt = now;
    }

    /// <summary>A usable pipeline has at least one open stage and exactly one Won and one Lost stage, with unique names and probabilities in range.</summary>
    public static string? ValidateStages(IReadOnlyList<(string Name, StageKind Kind, int Probability)> stages)
    {
        if (stages.Count is < 3 or > 20) return "A pipeline needs between 3 and 20 stages.";
        if (stages.Any(s => string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100)) return "Every stage needs a name (max 100 characters).";
        if (stages.Select(s => s.Name.Trim().ToLowerInvariant()).Distinct().Count() != stages.Count) return "Stage names must be unique.";
        if (stages.Count(s => s.Kind == StageKind.Won) != 1 || stages.Count(s => s.Kind == StageKind.Lost) != 1) return "A pipeline needs exactly one Won and one Lost stage.";
        if (!stages.Any(s => s.Kind == StageKind.Open)) return "A pipeline needs at least one open stage.";
        if (stages.Any(s => s.Probability is < 0 or > 100)) return "Probabilities are between 0 and 100.";
        return null;
    }
}
