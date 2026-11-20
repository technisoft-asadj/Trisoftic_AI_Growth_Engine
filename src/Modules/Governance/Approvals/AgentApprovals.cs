using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Governance.Approvals;

public sealed class AgentApprovals(GovernanceDbContext db, IEnumerable<IApprovalSubjectHandler> handlers, IClock clock, IAuditWriter audit) : IAgentApprovals
{
    public async Task<IReadOnlyList<PendingApprovalInfo>> ListPendingAsync(string subjectType, IReadOnlyCollection<string> subjectIds, CancellationToken ct = default)
        => subjectIds.Count == 0 ? [] : await db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending && a.SubjectType == subjectType && subjectIds.Contains(a.SubjectId))
            .Select(a => new PendingApprovalInfo(a.Id, a.SubjectId, a.SubjectVersion, a.ProductId, a.Title)).ToListAsync(ct);

    public async Task<Result> DecideAsync(Guid approvalId, ApprovalDecision decision, string comment, CancellationToken ct = default)
    {
        var a = await db.Approvals.FirstOrDefaultAsync(x => x.Id == approvalId, ct);
        if (a is null) return new Error("approval.not_found", "Approval not found", ErrorKind.NotFound);
        if (a.Status != ApprovalStatus.Pending) return new Error("approval.not_pending", $"Approval is already {a.Status}", ErrorKind.Conflict);
        if (a.RequestedBy == AgentIdentity.ReviewerId) return new Error("approval.self", "The reviewer cannot decide its own request", ErrorKind.Policy);
        if (string.IsNullOrWhiteSpace(comment)) return new Error("approval.comment", "The reviewer must give its reasons", ErrorKind.Validation);
        var handler = handlers.FirstOrDefault(h => h.SubjectType == a.SubjectType);
        if (handler is null) return new Error("approval.no_handler", $"No handler for {a.SubjectType}", ErrorKind.Permanent);

        var res = await handler.OnDecidedAsync(new ApprovalDecisionContext(a.Id, a.SubjectType, a.SubjectId, a.SubjectVersion, a.ProductId, decision, comment, AgentIdentity.ReviewerId), ct);
        if (!res.IsSuccess) return res;
        a.Decide(decision, AgentIdentity.ReviewerId, comment, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry($"approval.{decision.ToString().ToLowerInvariant()}", a.SubjectType, a.SubjectId, a.ProductId, a.Title,
            After: new { a.SubjectVersion, Comment = comment, ApprovalId = a.Id, By = AgentIdentity.ReviewerName }), ct);
        return Result.Ok();
    }
}
