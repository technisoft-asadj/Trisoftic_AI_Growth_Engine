using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Governance.Approvals;

public static class ApprovalEndpoints
{
    public sealed record ApprovalDto(Guid Id, string SubjectType, string SubjectId, int SubjectVersion, Guid? ProductId, string Title, string? Summary,
        ApprovalStatus Status, Guid? RequestedBy, DateTimeOffset RequestedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment, string RequiredPermission, bool CanDecide);
    public sealed record DecisionRequest(ApprovalDecision Decision, string? Comment);

    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/approvals").RequireAuthorization().WithTags("Approvals");

        g.MapGet("/", async (GovernanceDbContext db, ICurrentUser user, IEnumerable<IApprovalSubjectHandler> handlers, ApprovalStatus? status, string? subjectType, string? subjectId, Guid? productId, int? take, CancellationToken ct) =>
        {
            var q = db.Approvals.AsNoTracking().AsQueryable();
            var scope = await user.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(a => a.ProductId == null || scope.Contains(a.ProductId.Value));
            if (status is not null) q = q.Where(a => a.Status == status);
            if (!string.IsNullOrWhiteSpace(subjectType)) q = q.Where(a => a.SubjectType == subjectType);
            if (!string.IsNullOrWhiteSpace(subjectId)) q = q.Where(a => a.SubjectId == subjectId);
            if (productId is not null) q = q.Where(a => a.ProductId == productId);
            var rows = await q.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct);
            return Results.Ok(rows.Select(a => ToDto(a, handlers, user)));
        });

        g.MapGet("/{id:guid}", async (Guid id, GovernanceDbContext db, ICurrentUser user, IEnumerable<IApprovalSubjectHandler> handlers, CancellationToken ct) =>
        {
            var a = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null || (a.ProductId is { } p && !await user.CanAccessProductAsync(p, ct))) return Results.NotFound();
            return Results.Ok(ToDto(a, handlers, user));
        });

        g.MapPost("/{id:guid}/decision", async (Guid id, DecisionRequest req, GovernanceDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit,
            IEnumerable<IApprovalSubjectHandler> handlers, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = await db.Approvals.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null || (a.ProductId is { } p0 && !await user.CanAccessProductAsync(p0, ct))) return Results.NotFound();
            if (a.Status != ApprovalStatus.Pending) return Results.Problem(statusCode: 409, title: $"Approval is already {a.Status}");

            var handler = handlers.FirstOrDefault(h => h.SubjectType == a.SubjectType);
            if (handler is null) return Results.Problem(statusCode: 500, title: $"No handler for {a.SubjectType}");
            if (!user.HasPermission(handler.RequiredPermission)) return Results.Forbid();
            if (user.UserId is not { } me) return Results.Unauthorized();
            if (a.RequestedBy == me && !cfg.GetValue<bool>("Governance:AllowSelfApproval"))
                return Results.Problem(statusCode: 403, title: "Requesters cannot decide their own approvals");
            if (req.Decision != ApprovalDecision.Approved && string.IsNullOrWhiteSpace(req.Comment))
                return Results.Problem(statusCode: 400, title: "A comment is required when rejecting or requesting changes");

            // Apply to the subject first (idempotent, version-checked); then record the decision.
            var res = await handler.OnDecidedAsync(new ApprovalDecisionContext(a.Id, a.SubjectType, a.SubjectId, a.SubjectVersion, a.ProductId, req.Decision, req.Comment, me), ct);
            if (!res.IsSuccess)
                return Results.Problem(statusCode: res.Error!.Kind == ErrorKind.Conflict ? 409 : 400, title: res.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = res.Error.Code });

            a.Decide(req.Decision, me, req.Comment, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry($"approval.{req.Decision.ToString().ToLowerInvariant()}", a.SubjectType, a.SubjectId, a.ProductId, a.Title,
                After: new { a.SubjectVersion, req.Comment, ApprovalId = a.Id }), ct);
            return Results.Ok(ToDto(a, handlers, user));
        });

        return app;
    }

    private static ApprovalDto ToDto(ApprovalRecord a, IEnumerable<IApprovalSubjectHandler> handlers, ICurrentUser user)
    {
        var perm = handlers.FirstOrDefault(h => h.SubjectType == a.SubjectType)?.RequiredPermission ?? "";
        var canDecide = a.Status == ApprovalStatus.Pending && perm != "" && user.HasPermission(perm);
        return new(a.Id, a.SubjectType, a.SubjectId, a.SubjectVersion, a.ProductId, a.Title, a.Summary, a.Status, a.RequestedBy, a.RequestedAt,
            a.DecidedBy, a.DecidedAt, a.DecisionComment, perm, canDecide);
    }
}
