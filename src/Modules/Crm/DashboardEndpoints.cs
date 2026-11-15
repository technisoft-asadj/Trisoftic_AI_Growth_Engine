using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public static class DashboardMath
{
    /// <summary>Null when there is nothing to divide by: "no data" must never be shown as 0%.</summary>
    public static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round(part * 100.0 / whole, 1);

    public static double? Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return null;
        var s = xs.OrderBy(x => x).ToArray();
        return Math.Round(s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2, 1);
    }

    public static double? Mean(IReadOnlyList<double> xs) => xs.Count == 0 ? null : Math.Round(xs.Average(), 1);
}

public static class DashboardEndpoints
{
    private const int RowCap = 20_000;

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/crm/dashboard", async (CrmDbContext db, ICurrentUser u, IClock clock, Guid? productId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var now = clock.UtcNow;
            var end = to ?? now; var start = from ?? end.AddDays(-30);
            if (end <= start || end - start > TimeSpan.FromDays(366)) return Results.Problem(statusCode: 400, title: "Provide a period of up to 366 days");
            if (productId is { } pid && !await u.CanAccessProductAsync(pid, ct)) return Results.NotFound();
            var scope = await u.AccessibleProductIdsAsync(ct);

            IQueryable<Lead> leadsQ = db.Leads.AsNoTracking();
            IQueryable<Opportunity> oppsQ = db.Opportunities.AsNoTracking();
            if (scope is not null) { leadsQ = leadsQ.Where(l => scope.Contains(l.ProductId)); oppsQ = oppsQ.Where(o => scope.Contains(o.ProductId)); }
            if (productId is not null) { leadsQ = leadsQ.Where(l => l.ProductId == productId); oppsQ = oppsQ.Where(o => o.ProductId == productId); }

            var leads = await leadsQ.Where(l => l.CreatedAt >= start && l.CreatedAt <= end).OrderBy(l => l.CreatedAt).Take(RowCap + 1)
                .Select(l => new { l.Id, l.Status, l.Source, l.CampaignId, l.CreatedAt, l.FirstContactedAt, l.QualifiedAt, l.ConvertedOpportunityId }).ToListAsync(ct);
            var truncated = leads.Count > RowCap; if (truncated) leads.RemoveAt(leads.Count - 1);

            // Deals that came from the leads in this period (for the funnel's last step and attribution).
            var leadIds = leads.Where(l => l.ConvertedOpportunityId != null).Select(l => l.ConvertedOpportunityId!.Value).ToList();
            var fromLeads = await oppsQ.Where(o => leadIds.Contains(o.Id)).Select(o => new { o.Id, o.Status, o.LeadId, o.Currency, o.RevenueAmount }).ToListAsync(ct);
            var wonFromLeads = fromLeads.Where(o => o.Status == OpportunityStatus.Won).ToList();

            var responseMinutes = leads.Where(l => l.FirstContactedAt != null).Select(l => (l.FirstContactedAt!.Value - l.CreatedAt).TotalMinutes).Select(m => Math.Max(0, m)).ToList();

            // Open pipeline right now (not limited to the period).
            var open = await (from o in oppsQ join s in db.Stages.AsNoTracking() on o.StageId equals s.Id where o.Status == OpportunityStatus.Open
                              select new { o.Value, o.Currency, o.ProbabilityOverride, s.Probability, o.ExpectedCloseDate }).ToListAsync(ct);
            var today = DateOnly.FromDateTime(now.UtcDateTime);

            // Deals closed in the period.
            var closed = await oppsQ.Where(o => o.ClosedAt >= start && o.ClosedAt <= end && o.Status != OpportunityStatus.Open)
                .Select(o => new { o.Status, o.Currency, o.RevenueAmount, o.CreatedAt, o.ClosedAt }).ToListAsync(ct);
            var won = closed.Where(c => c.Status == OpportunityStatus.Won).ToList();
            var lost = closed.Count - won.Count;

            var overdueLeads = await leadsQ.CountAsync(l => l.NextActionDue != null && l.NextActionDue < now && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified), ct);
            var overdueActivities = await (from a in db.Activities.AsNoTracking() join o in oppsQ on a.OpportunityId equals o.Id
                                           where a.CompletedAt == null && a.DueAt != null && a.DueAt < now select a.Id).CountAsync(ct);

            var campaignRows = leads.Where(l => l.CampaignId != null).GroupBy(l => l.CampaignId!.Value).Select(g => new
            {
                campaignId = g.Key, leads = g.Count(), qualified = g.Count(l => l.QualifiedAt != null), converted = g.Count(l => l.ConvertedOpportunityId != null),
                won = g.Count(l => wonFromLeads.Any(w => w.Id == l.ConvertedOpportunityId)),
            }).OrderByDescending(x => x.leads).Take(20).ToList();

            int created = leads.Count, contacted = leads.Count(l => l.FirstContactedAt != null), qualified = leads.Count(l => l.QualifiedAt != null),
                converted = leads.Count(l => l.ConvertedOpportunityId != null), wonCount = wonFromLeads.Count;

            return Results.Ok(new
            {
                meta = new
                {
                    asOf = now, periodFrom = start, periodTo = end, source = "TriSoftic CRM (leads, opportunities)", productId,
                    truncated,
                    notes = new[] { "A value of null means there is nothing to measure yet; it is not zero.", "Attribution: a lead counts as attributed only when it carries a campaign; others are unknown-source." },
                },
                leads = new
                {
                    created,
                    byStatus = Enum.GetValues<LeadStatus>().ToDictionary(s => s.ToString(), s => leads.Count(l => l.Status == s)),
                    bySource = leads.GroupBy(l => l.Source).Select(g => new { source = g.Key, count = g.Count() }).OrderByDescending(x => x.count).Take(10),
                    attributedToCampaign = leads.Count(l => l.CampaignId != null),
                    unknownSource = leads.Count(l => l.CampaignId == null),
                },
                funnel = new
                {
                    created, contacted, qualified, converted, won = wonCount,
                    contactedRatePct = DashboardMath.Rate(contacted, created), qualifiedRatePct = DashboardMath.Rate(qualified, contacted),
                    convertedRatePct = DashboardMath.Rate(converted, qualified), wonRatePct = DashboardMath.Rate(wonCount, converted),
                    leadToWonPct = DashboardMath.Rate(wonCount, created),
                },
                responseTime = new { sampleSize = responseMinutes.Count, averageMinutes = DashboardMath.Mean(responseMinutes), medianMinutes = DashboardMath.Median(responseMinutes), leadsNotYetContacted = created - contacted },
                pipeline = new
                {
                    openDeals = open.Count,
                    // Different currencies are reported side by side, never summed.
                    openValueByCurrency = open.GroupBy(o => o.Currency).Select(g => new { currency = g.Key, value = g.Sum(o => o.Value), weighted = Math.Round(g.Sum(o => o.Value * (o.ProbabilityOverride ?? o.Probability) / 100m), 2) }),
                    pastExpectedClose = open.Count(o => o.ExpectedCloseDate != null && o.ExpectedCloseDate < today),
                },
                outcomes = new
                {
                    won = won.Count, lost,
                    winRatePct = DashboardMath.Rate(won.Count, closed.Count),
                    revenueByCurrency = won.GroupBy(w => w.Currency).Select(g => new { currency = g.Key, revenue = g.Sum(w => w.RevenueAmount ?? 0m) }),
                    averageSalesCycleDays = DashboardMath.Mean(won.Select(w => (w.ClosedAt!.Value - w.CreatedAt).TotalDays).ToList()),
                },
                followUps = new { overdueLeads, overdueSalesActivities = overdueActivities },
                byCampaign = campaignRows,
            });
        }).RequireAuthorization().WithTags("Dashboard");
        return app;
    }
}
