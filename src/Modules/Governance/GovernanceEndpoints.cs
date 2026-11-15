using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Governance.Approvals;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Governance;

public static class GovernanceEndpoints
{
    public sealed record AuditEventDto(Guid Id, DateTimeOffset OccurredAt, string? ActorEmail, string Action, string SubjectType, string? SubjectId, Guid? ProductId, string? Summary, string? CorrelationId);
    public sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);
    public sealed record PauseDto(string Scope, bool IsPaused, string? Reason, DateTimeOffset? ChangedAt);
    public sealed record SetPauseRequest(bool IsPaused, string? Reason);

    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/governance").RequireAuthorization().WithTags("Governance");

        g.MapGet("/audit", async (GovernanceDbContext db, ICurrentUser user, string? subjectType, string? subjectId, Guid? productId, string? cursor, int? take, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.AuditRead)) return Results.Forbid();
            var q = db.AuditEvents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(subjectType)) q = q.Where(a => a.SubjectType == subjectType);
            if (!string.IsNullOrWhiteSpace(subjectId)) q = q.Where(a => a.SubjectId == subjectId);
            if (productId is not null) q = q.Where(a => a.ProductId == productId);
            if (Guid.TryParse(cursor, out var c)) q = q.Where(a => a.Id.CompareTo(c) < 0);
            var n = Math.Clamp(take ?? 50, 1, 200);
            var rows = await q.OrderByDescending(a => a.Id).Take(n + 1).ToListAsync(ct);
            var next = rows.Count > n ? rows[n - 1].Id.ToString() : null;
            return Results.Ok(new PageDto<AuditEventDto>(
                rows.Take(n).Select(a => new AuditEventDto(a.Id, a.OccurredAt, a.ActorEmail, a.Action, a.SubjectType, a.SubjectId, a.ProductId, a.Summary, a.CorrelationId)).ToList(), next));
        });

        g.MapGet("/pause", async (GovernanceDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.GovernancePause) && !user.HasPermission(Permissions.AuditRead)) return Results.Forbid();
            var saved = await db.PauseFlags.AsNoTracking().ToListAsync(ct);
            var all = Enum.GetNames<PauseScope>().Select(s =>
            {
                var f = saved.FirstOrDefault(x => x.Scope == s);
                return new PauseDto(s, f?.IsPaused ?? false, f?.Reason, f?.ChangedAt);
            });
            return Results.Ok(all);
        });

        g.MapPut("/pause/{scope}", async (string scope, SetPauseRequest req, GovernanceDbContext db, ICurrentUser user, IAuditWriter audit, IClock clock, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.GovernancePause)) return Results.Forbid();
            if (!Enum.TryParse<PauseScope>(scope, true, out var s)) return Results.Problem(statusCode: 400, title: "Unknown pause scope");
            var key = s.ToString();
            var flag = await db.PauseFlags.FirstOrDefaultAsync(p => p.Scope == key, ct);
            var before = flag?.IsPaused ?? false;
            if (flag is null) db.PauseFlags.Add(flag = new() { Scope = key });
            flag.IsPaused = req.IsPaused; flag.Reason = req.Reason; flag.ChangedBy = user.UserId; flag.ChangedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry(req.IsPaused ? "governance.pause.enabled" : "governance.pause.disabled", "PauseFlag", key,
                Summary: req.Reason, Before: new { IsPaused = before }, After: new { req.IsPaused }), ct);
            return Results.Ok(new PauseDto(key, flag.IsPaused, flag.Reason, flag.ChangedAt));
        });

        app.MapApprovalEndpoints();
        return app;
    }
}
