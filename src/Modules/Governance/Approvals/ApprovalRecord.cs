using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Governance.Approvals;

public sealed class ApprovalRecord : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string SubjectType { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public int SubjectVersion { get; set; }
    public Guid? ProductId { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }

    /// <summary>The only legal transitions. Everything else throws.</summary>
    public void Decide(ApprovalDecision decision, Guid by, string? comment, DateTimeOffset at)
    {
        if (Status != ApprovalStatus.Pending)
            throw new BuildingBlocks.Results.IllegalTransitionException($"Approval {Id} is {Status}, not Pending");
        Status = decision switch
        {
            ApprovalDecision.Approved => ApprovalStatus.Approved,
            ApprovalDecision.Rejected => ApprovalStatus.Rejected,
            ApprovalDecision.ChangesRequested => ApprovalStatus.ChangesRequested,
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        DecidedBy = by; DecidedAt = at; DecisionComment = comment;
    }

    public void Withdraw(DateTimeOffset at)
    {
        if (Status != ApprovalStatus.Pending) throw new BuildingBlocks.Results.IllegalTransitionException($"Only Pending approvals can be withdrawn (was {Status})");
        Status = ApprovalStatus.Withdrawn; DecidedAt = at;
    }

    public void Supersede(DateTimeOffset at)
    {
        if (Status != ApprovalStatus.Pending) throw new BuildingBlocks.Results.IllegalTransitionException($"Only Pending approvals can be superseded (was {Status})");
        Status = ApprovalStatus.Superseded; DecidedAt = at;
    }
}
