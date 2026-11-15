using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Content;

/// <summary>Applies approval decisions to a content item. Idempotent, and refuses decisions for any version other than the current one.</summary>
public sealed class ContentApprovalHandler(ContentDbContext db) : IApprovalSubjectHandler
{
    public const string Subject = "ContentItem";
    public string SubjectType => Subject;
    public string RequiredPermission => Permissions.ContentApprove;

    public async Task<Result> OnDecidedAsync(ApprovalDecisionContext c, CancellationToken ct)
    {
        if (!Guid.TryParse(c.SubjectId, out var id)) return new Error("content.bad_subject", "Invalid subject id", ErrorKind.Validation);
        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return new Error("content.not_found", "Content not found", ErrorKind.NotFound);
        if (item.CurrentVersion != c.SubjectVersion)
            return new Error("content.stale", $"The content is now at v{item.CurrentVersion}; this approval request was for v{c.SubjectVersion}.", ErrorKind.Conflict);

        var target = c.Decision switch { ApprovalDecision.Approved => ContentStatus.Approved, ApprovalDecision.Rejected => ContentStatus.Rejected, _ => ContentStatus.Draft };
        if (item.Status == target) return Result.Ok();   // idempotent replay
        if (item.Status != ContentStatus.InReview) return new Error("content.stale", $"The content is {item.Status}, not in review.", ErrorKind.Conflict);

        switch (c.Decision)
        {
            case ApprovalDecision.Approved: item.Approve(c.SubjectVersion); break;
            case ApprovalDecision.Rejected: item.Reject(c.SubjectVersion); break;
            default: item.ReturnForChanges(c.SubjectVersion); break;
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
