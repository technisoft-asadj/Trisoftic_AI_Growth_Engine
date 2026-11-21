using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Planning;

/// <summary>The learnings register: hypotheses added by reviews (or by a person), judged by later reviews, and put in front of every new plan until they are settled.</summary>
public sealed class LearningService(PlanningDbContext db, IClock clock, IAuditWriter audit)
{
    public const int MaxOpen = 8;
    public const int RetireAfterInconclusiveTests = 3;

    public async Task<IReadOnlyList<OpenHypothesis>> OpenAsync(Guid productId, CancellationToken ct)
        => await db.Learnings.AsNoTracking().Where(l => l.ProductId == productId && l.Status == LearningStatus.Open).OrderBy(l => l.CreatedAt).Take(MaxOpen)
            .Select(l => new OpenHypothesis(l.Id, l.Statement, l.HowToTest, l.TimesTested, l.Evidence)).ToListAsync(ct);

    /// <summary>Records what a review concluded: verdicts on open hypotheses, then any new ones (skipping near-duplicates, and never holding more than <see cref="MaxOpen"/> open).</summary>
    public async Task ApplyReviewAsync(Guid productId, Guid reviewId, ReviewResult r, CancellationToken ct)
    {
        var all = await db.Learnings.Where(l => l.ProductId == productId && (l.Status == LearningStatus.Open)).ToListAsync(ct);
        var now = clock.UtcNow;
        foreach (var u in r.Updates)
        {
            var l = all.FirstOrDefault(x => x.Id == u.Id);
            if (l is null) continue;
            l.Evidence = u.Evidence.Trim().Length > 1900 ? u.Evidence.Trim()[..1900] : u.Evidence.Trim(); l.Confidence = r.Confidence; l.UpdatedAt = now;
            switch (u.Outcome)
            {
                case "supported": l.Status = LearningStatus.Supported; break;
                case "contradicted": l.Status = LearningStatus.Contradicted; break;
                default:
                    if (l.TimesTested >= RetireAfterInconclusiveTests) { l.Status = LearningStatus.Retired; l.Evidence = "Inconclusive after " + l.TimesTested + " tests. " + l.Evidence; }
                    break;
            }
        }
        var open = all.Count(l => l.Status == LearningStatus.Open);
        foreach (var h in r.Hypotheses.Take(3))
        {
            if (open >= MaxOpen) break;
            if (all.Any(x => AutopilotGates.Similarity(x.Statement, h.Statement) >= 0.8)) continue;
            var l = new Learning { ProductId = productId, Statement = h.Statement.Trim(), HowToTest = string.IsNullOrWhiteSpace(h.HowToTest) ? null : h.HowToTest.Trim(), Status = LearningStatus.Open, SourceReviewId = reviewId, CreatedAt = now, UpdatedAt = now };
            db.Learnings.Add(l); all.Add(l); open++;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("learnings.updated", "Learning", reviewId.ToString(), productId, "Review recorded", After: new { updates = r.Updates.Count, added = r.Hypotheses.Count }), ct);
    }

    /// <summary>A new plan is about to test these hypotheses.</summary>
    public async Task MarkTestedAsync(Guid planId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await db.Learnings.Where(l => ids.Contains(l.Id)).ExecuteUpdateAsync(x => x.SetProperty(l => l.TimesTested, l => l.TimesTested + 1).SetProperty(l => l.LastTestedPlanId, planId).SetProperty(l => l.UpdatedAt, clock.UtcNow), ct);
    }
}

public static class LearningEndpoints
{
    public sealed record AddRequest(string? Statement, string? HowToTest);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/products/{productId:guid}/learnings", async (Guid productId, PlanningDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var rows = await db.Learnings.AsNoTracking().Where(l => l.ProductId == productId).OrderBy(l => l.Status).ThenByDescending(l => l.UpdatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(l => new { l.Id, l.Statement, l.HowToTest, status = l.Status.ToString(), l.Confidence, l.Evidence, l.TimesTested, l.CreatedAt, l.UpdatedAt, fromReview = l.SourceReviewId != null }));
        });

        // A person can add a hypothesis of their own; it is tested like any other.
        g.MapPost("/products/{productId:guid}/learnings", async (Guid productId, AddRequest req, PlanningDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var s = req.Statement?.Trim() ?? "";
            if (s.Length is < 5 or > 300) return Results.Problem(statusCode: 400, title: "Write the hypothesis in one sentence (5 to 300 characters).");
            if (await db.Learnings.CountAsync(l => l.ProductId == productId && l.Status == LearningStatus.Open, ct) >= LearningService.MaxOpen) return Results.Problem(statusCode: 409, title: $"There are already {LearningService.MaxOpen} open hypotheses. Retire one first.");
            var l = new Learning { ProductId = productId, Statement = s, HowToTest = string.IsNullOrWhiteSpace(req.HowToTest) ? null : req.HowToTest.Trim(), Status = LearningStatus.Open, CreatedBy = u.UserId, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
            db.Learnings.Add(l); await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("learnings.added", "Learning", l.Id.ToString(), productId, s), ct);
            return Results.Created($"/api/v1/products/{productId}/learnings", new { l.Id });
        });

        g.MapPost("/learnings/{id:guid}/retire", async (Guid id, PlanningDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var l = await db.Learnings.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (l is null || !u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(l.ProductId, ct)) return Results.NotFound();
            if (l.Status != LearningStatus.Open) return Results.Problem(statusCode: 409, title: "Only an open hypothesis can be retired.");
            l.Status = LearningStatus.Retired; l.UpdatedAt = clock.UtcNow; await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("learnings.retired", "Learning", id.ToString(), l.ProductId, l.Statement), ct);
            return Results.NoContent();
        });
    }
}
