using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public enum ApprovalStatus { Pending, Approved, Rejected, ChangesRequested, Withdrawn, Superseded }

public enum ApprovalDecision { Approved, Rejected, ChangesRequested }

/// <summary>A request to approve ONE specific version of a subject. Approving v2 never approves v3.</summary>
public sealed record ApprovalRequest(
    string SubjectType,
    string SubjectId,
    int SubjectVersion,
    Guid? ProductId,
    string Title,
    string? Summary = null);

public sealed record ApprovalDecisionContext(
    Guid ApprovalId,
    string SubjectType,
    string SubjectId,
    int SubjectVersion,
    Guid? ProductId,
    ApprovalDecision Decision,
    string? Comment,
    Guid DecidedBy);

public interface IApprovalService
{
    /// <summary>Creates a Pending approval; any earlier Pending approval for the same subject is marked Superseded.</summary>
    Task<Result<Guid>> RequestAsync(ApprovalRequest request, CancellationToken ct = default);

    /// <summary>Withdraws the Pending approval for the subject (e.g. author edited after submitting).</summary>
    Task WithdrawAsync(string subjectType, string subjectId, CancellationToken ct = default);

    /// <summary>Status of the most recent approval for exactly this subject version, or null if none.</summary>
    Task<ApprovalStatus?> GetStatusAsync(string subjectType, string subjectId, int subjectVersion, CancellationToken ct = default);
}

/// <summary>
/// Implemented by each module that owns an approvable subject. The Governance engine enforces who may decide
/// (RequiredPermission + product scope + no self-approval); the handler applies the decision to its own aggregate.
/// OnDecidedAsync MUST be idempotent and MUST fail (Conflict) if the subject version is no longer current.
/// </summary>
public interface IApprovalSubjectHandler
{
    string SubjectType { get; }
    string RequiredPermission { get; }
    Task<Result> OnDecidedAsync(ApprovalDecisionContext context, CancellationToken ct);
}
