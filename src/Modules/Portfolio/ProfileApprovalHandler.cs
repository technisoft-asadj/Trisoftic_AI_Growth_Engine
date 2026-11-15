using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Portfolio.Domain;
using TriSoftic.GrowthEngine.Modules.Portfolio.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Portfolio;

/// <summary>Applies approval decisions to a product profile version. Idempotent and version-checked.</summary>
public sealed class ProfileApprovalHandler(PortfolioDbContext db, IClock clock) : IApprovalSubjectHandler
{
    public const string Subject = "ProductProfile";
    public string SubjectType => Subject;
    public string RequiredPermission => Permissions.ProductProfileApprove;

    public async Task<Result> OnDecidedAsync(ApprovalDecisionContext c, CancellationToken ct)
    {
        if (!Guid.TryParse(c.SubjectId, out var productId)) return new Error("profile.bad_subject", "Invalid subject id", ErrorKind.Validation);
        var v = await db.ProfileVersions.FirstOrDefaultAsync(x => x.ProductId == productId && x.Number == c.SubjectVersion, ct);
        if (v is null) return new Error("profile.not_found", "Profile version not found", ErrorKind.NotFound);

        var target = c.Decision switch
        {
            ApprovalDecision.Approved => ProfileStatus.Approved,
            ApprovalDecision.Rejected => ProfileStatus.Rejected,
            _ => ProfileStatus.Draft,
        };
        if (v.Status == target && v.DecidedBy == c.DecidedBy) return Result.Ok();   // idempotent replay
        if (v.Status != ProfileStatus.InReview)
            return new Error("profile.stale", $"Profile v{v.Number} is {v.Status}; the approval request is stale", ErrorKind.Conflict);

        var now = clock.UtcNow;
        switch (c.Decision)
        {
            case ApprovalDecision.Approved:
                var product = await db.Products.FirstAsync(p => p.Id == productId, ct);
                foreach (var old in await db.ProfileVersions.Where(x => x.ProductId == productId && x.Status == ProfileStatus.Approved).ToListAsync(ct))
                    old.Supersede();
                v.Approve(c.DecidedBy, c.Comment, now);
                product.ApprovedProfileVersion = v.Number; product.UpdatedAt = now;
                break;
            case ApprovalDecision.Rejected: v.Reject(c.DecidedBy, c.Comment, now); break;
            default: v.ReturnForChanges(c.DecidedBy, c.Comment, now); break;
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
