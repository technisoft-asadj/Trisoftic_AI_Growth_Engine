using Microsoft.EntityFrameworkCore;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Governance.Approvals;

public sealed class ApprovalService(
    GovernanceDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit,
    IEnumerable<IApprovalSubjectHandler> handlers) : IApprovalService
{
    public async Task<Result<Guid>> RequestAsync(ApprovalRequest r, CancellationToken ct = default)
    {
        if (!handlers.Any(h => h.SubjectType == r.SubjectType))
            return new Error("approval.unknown_subject", $"No approval handler for subject type '{r.SubjectType}'", ErrorKind.Permanent);

        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var old in await db.Approvals.Where(a => a.SubjectType == r.SubjectType && a.SubjectId == r.SubjectId && a.Status == ApprovalStatus.Pending).ToListAsync(ct))
            old.Supersede(now);
        await db.SaveChangesAsync(ct);   // free the unique pending slot first

        var rec = new ApprovalRecord
        {
            SubjectType = r.SubjectType, SubjectId = r.SubjectId, SubjectVersion = r.SubjectVersion, ProductId = r.ProductId,
            Title = r.Title, Summary = r.Summary, RequestedBy = user.UserId, RequestedAt = now,
        };
        db.Approvals.Add(rec);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new Error("approval.concurrent_request", "Another approval request for this subject was created concurrently", ErrorKind.Conflict);
        }

        await audit.WriteAsync(new AuditEntry("approval.requested", r.SubjectType, r.SubjectId, r.ProductId, r.Title, After: new { r.SubjectVersion, ApprovalId = rec.Id }), ct);
        return rec.Id;
    }

    public async Task WithdrawAsync(string subjectType, string subjectId, CancellationToken ct = default)
    {
        var pending = await db.Approvals.FirstOrDefaultAsync(a => a.SubjectType == subjectType && a.SubjectId == subjectId && a.Status == ApprovalStatus.Pending, ct);
        if (pending is null) return;
        pending.Withdraw(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("approval.withdrawn", subjectType, subjectId, pending.ProductId, pending.Title, After: new { pending.SubjectVersion }), ct);
    }

    public async Task<ApprovalStatus?> GetStatusAsync(string subjectType, string subjectId, int subjectVersion, CancellationToken ct = default) =>
        await db.Approvals.AsNoTracking()
            .Where(a => a.SubjectType == subjectType && a.SubjectId == subjectId && a.SubjectVersion == subjectVersion)
            .OrderByDescending(a => a.Id).Select(a => (ApprovalStatus?)a.Status).FirstOrDefaultAsync(ct);
}
