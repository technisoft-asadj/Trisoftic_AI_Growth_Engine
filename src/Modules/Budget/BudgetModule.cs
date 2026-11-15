using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Budget;

public static partial class BudgetModule
{
    public sealed record AllocIn(string Label, decimal Amount);
    public sealed record BudgetIn(BudgetScope Scope, Guid? ProductId, Guid? CampaignId, string Name, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd, decimal HardCap,
        RiskTolerance? Risk, int? WarnAtPct, int? StopAtPct, AllocIn[]? Allocations);
    public sealed record ChangeIn(ChangeKind Kind, decimal? ProposedCap, AllocIn[]? Allocations, string Reason);
    public sealed record SpendIn(SpendKind Kind, decimal Amount, Guid? AllocationId, DateOnly? OccurredOn, string? Note);
    public sealed record VoidIn(string Reason);
    public sealed record PlanIn(string? ResultMetric);
    public sealed record AdoptIn(string Scenario);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyRx();
    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")] private static partial Regex MetricRx();

    public static IServiceCollection AddBudgetModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<BudgetDbContext>(c, BudgetDbContext.Schema);
        s.AddScoped<IApprovalSubjectHandler, BudgetChangeApprovalHandler>();
        s.AddScoped<ISubjectStateProvider, BudgetStateProvider>();
        return s;
    }

    private static async Task<(SpendTotals All, Dictionary<Guid, SpendTotals> ByAllocation)> TotalsAsync(BudgetDbContext db, Guid budgetId, CancellationToken ct)
    {
        var rows = await db.Spend.AsNoTracking().Where(s => s.BudgetId == budgetId && !s.Voided).Select(s => new { s.Kind, s.Amount, s.AllocationId }).ToListAsync(ct);
        return (SpendTotals.From(rows.Select(r => (r.Kind, r.Amount))),
            rows.Where(r => r.AllocationId != null).GroupBy(r => r.AllocationId!.Value).ToDictionary(g => g.Key, g => SpendTotals.From(g.Select(r => (r.Kind, r.Amount)))));
    }

    private static async Task<bool> CanSeeAsync(ICurrentUser u, MarketingBudget b, CancellationToken ct)
        => b.ProductId is { } p ? await u.CanAccessProductAsync(p, ct) : await u.AccessibleProductIdsAsync(ct) is null;   // a company-wide budget needs access to every product

    private static object Dto(MarketingBudget b, SpendTotals t, IReadOnlyList<BudgetAllocation> allocs, Dictionary<Guid, SpendTotals> byAlloc) => new
    {
        b.Id, b.Scope, b.ProductId, b.CampaignId, b.Name, b.Currency, b.PeriodStart, b.PeriodEnd, b.HardCap, b.Risk, b.WarnAtPct, b.StopAtPct, b.Status, b.Version,
        totals = new { t.Recommended, t.Approved, t.Committed, t.Actual, exposure = t.Exposure, remaining = b.HardCap - t.Exposure, spentPct = b.HardCap > 0 ? Math.Round(t.Exposure * 100m / b.HardCap, 1) : (decimal?)null },
        level = BudgetRules.Level(t.Exposure, b.HardCap, b.WarnAtPct, b.StopAtPct),
        allocations = allocs.OrderBy(a => a.SortOrder).Select(a =>
        {
            var at = byAlloc.GetValueOrDefault(a.Id, SpendTotals.Zero);
            return new { a.Id, a.Label, a.Amount, at.Recommended, at.Approved, at.Committed, at.Actual, remaining = a.Amount - at.Exposure };
        }),
    };

    public static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/budget").RequireAuthorization().WithTags("Budget");

        g.MapGet("/budgets", async (BudgetDbContext db, ICurrentUser u, Guid? productId, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.BudgetRead)) return Results.Forbid();
            var q = db.Budgets.AsNoTracking().AsQueryable();
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(b => b.ProductId != null && scope.Contains(b.ProductId.Value));
            if (productId is not null) q = q.Where(b => b.ProductId == productId);
            var rows = await q.OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync(ct);
            var ids = rows.Select(b => b.Id).ToList();
            var spend = await db.Spend.AsNoTracking().Where(s => ids.Contains(s.BudgetId) && !s.Voided).Select(s => new { s.BudgetId, s.Kind, s.Amount }).ToListAsync(ct);
            return Results.Ok(rows.Select(b =>
            {
                var t = SpendTotals.From(spend.Where(s => s.BudgetId == b.Id).Select(s => (s.Kind, s.Amount)));
                return new { b.Id, b.Name, b.Scope, b.ProductId, b.Currency, b.HardCap, b.Status, b.PeriodStart, b.PeriodEnd, exposure = t.Exposure, level = BudgetRules.Level(t.Exposure, b.HardCap, b.WarnAtPct, b.StopAtPct) };
            }));
        });

        g.MapPost("/budgets", async (BudgetIn req, BudgetDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.BudgetPropose)) return Results.Forbid();
            if (await Validate(req, u, ct) is { } err) return Results.Problem(statusCode: 400, title: err);
            var b = new MarketingBudget
            {
                Scope = req.Scope, ProductId = req.Scope == BudgetScope.Company ? null : req.ProductId, CampaignId = req.Scope == BudgetScope.Campaign ? req.CampaignId : null, Name = req.Name.Trim(),
                Currency = req.Currency.ToUpperInvariant(), PeriodStart = req.PeriodStart, PeriodEnd = req.PeriodEnd, HardCap = req.HardCap, Risk = req.Risk ?? RiskTolerance.Balanced,
                WarnAtPct = req.WarnAtPct ?? 80, StopAtPct = req.StopAtPct ?? 100, Status = BudgetStatus.Draft, CreatedBy = u.UserId, CreatedAt = clock.UtcNow,
            };
            db.Budgets.Add(b);
            AddAllocations(db, b.Id, req.Allocations);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("budget.created", "Budget", b.Id.ToString(), b.ProductId, b.Name, After: new { b.Scope, b.Currency, b.HardCap, b.Risk }), ct);
            return Results.Created($"/api/v1/budget/budgets/{b.Id}", new { b.Id });
        });

        g.MapGet("/budgets/{id:guid}", async (Guid id, BudgetDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var b = await db.Budgets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.BudgetRead) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            var allocs = await db.Allocations.AsNoTracking().Where(a => a.BudgetId == id).ToListAsync(ct);
            var (all, by) = await TotalsAsync(db, id, ct);
            var changes = await db.Changes.AsNoTracking().Where(c => c.BudgetId == id).OrderByDescending(c => c.RequestedAt).Take(50).ToListAsync(ct);
            return Results.Ok(new
            {
                budget = Dto(b, all, allocs, by),
                changes = changes.Select(c => new { c.Id, c.Kind, c.Status, c.CurrentCap, c.ProposedCap, c.Reason, c.RequestedAt, c.DecidedAt, c.DecisionNote, allocations = JsonSerializer.Deserialize<AllocIn[]>(c.ProposedAllocationsJson, Json) }),
            });
        });

        // Drafts are a proposal and can be edited freely. A launched budget changes only through approved change requests.
        g.MapPut("/budgets/{id:guid}", async (Guid id, BudgetIn req, BudgetDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var b = await db.Budgets.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.BudgetPropose) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            if (b.Status != BudgetStatus.Draft) return Results.Problem(statusCode: 409, title: "A launched budget cannot be edited directly. Request a cap increase or a reallocation, which needs approval.");
            if (await db.Changes.AnyAsync(c => c.BudgetId == id && c.Status == ChangeStatus.Pending, ct)) return Results.Problem(statusCode: 409, title: "Withdraw the pending launch request before editing");
            if (await Validate(req, u, ct) is { } err) return Results.Problem(statusCode: 400, title: err);
            b.Name = req.Name.Trim(); b.Currency = req.Currency.ToUpperInvariant(); b.PeriodStart = req.PeriodStart; b.PeriodEnd = req.PeriodEnd; b.HardCap = req.HardCap;
            b.Risk = req.Risk ?? b.Risk; b.WarnAtPct = req.WarnAtPct ?? 80; b.StopAtPct = req.StopAtPct ?? 100;
            db.Allocations.RemoveRange(db.Allocations.Where(a => a.BudgetId == id));
            AddAllocations(db, id, req.Allocations);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("budget.updated", "Budget", id.ToString(), b.ProductId, b.Name, After: new { b.HardCap, b.Currency }), ct);
            return Results.NoContent();
        });

        MapChanges(g);
        MapSpend(g);
        MapPlanner(g);
        return app;
    }

    private static async Task<string?> Validate(BudgetIn r, ICurrentUser u, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) return "A name is required";
        if (!CurrencyRx().IsMatch((r.Currency ?? "").ToUpperInvariant())) return "Use a 3-letter currency code such as USD or PKR";
        if (r.PeriodEnd <= r.PeriodStart) return "The end date must be after the start date";
        if (r.HardCap <= 0 || r.HardCap > BudgetRules.MaxAmount) return "The hard cap must be greater than zero";
        if (BudgetRules.ValidateThresholds(r.WarnAtPct ?? 80, r.StopAtPct ?? 100) is { } t) return t;
        if (BudgetRules.ValidateAllocations(r.HardCap, (r.Allocations ?? []).Select(a => (a.Label ?? "", a.Amount)).ToList()) is { } a) return a;
        switch (r.Scope)
        {
            case BudgetScope.Company:
                if (await u.AccessibleProductIdsAsync(ct) is not null) return "A company-wide budget needs access to every product";
                break;
            case BudgetScope.Product:
                if (r.ProductId is not { } p || !await u.CanAccessProductAsync(p, ct)) return "Choose a product you can access";
                break;
            case BudgetScope.Campaign:
                if (r.ProductId is not { } pc || !await u.CanAccessProductAsync(pc, ct)) return "Choose a product you can access";
                if (r.CampaignId is null) return "Choose the campaign";
                break;
        }
        return null;
    }

    private static void AddAllocations(BudgetDbContext db, Guid budgetId, AllocIn[]? allocs)
    {
        var i = 0;
        foreach (var a in allocs ?? []) db.Allocations.Add(new BudgetAllocation { BudgetId = budgetId, Label = a.Label.Trim(), Amount = a.Amount, SortOrder = i++ });
    }

    // ---------------------------------------------------------------- changes (launch / increase / reallocation) ----------------------------------------------------------------

    private static void MapChanges(RouteGroupBuilder g)
    {
        g.MapPost("/budgets/{id:guid}/changes", async (Guid id, ChangeIn req, BudgetDbContext db, ICurrentUser u, IApprovalService approvals, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var b = await db.Budgets.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.BudgetPropose) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length < 3 || req.Reason.Length > 1000) return Results.Problem(statusCode: 400, title: "Say why (a few words at least)");
            if (b.Status == BudgetStatus.Closed) return Results.Problem(statusCode: 409, title: "This budget is closed");
            if (await db.Changes.AnyAsync(c => c.BudgetId == id && c.Status == ChangeStatus.Pending, ct)) return Results.Problem(statusCode: 409, title: "There is already a pending request for this budget. Wait for the decision or withdraw it");

            var current = await db.Allocations.AsNoTracking().Where(a => a.BudgetId == id).OrderBy(a => a.SortOrder).ToListAsync(ct);
            var (_, by) = await TotalsAsync(db, id, ct);
            decimal cap = b.HardCap; AllocIn[] allocs = [.. current.Select(a => new AllocIn(a.Label, a.Amount))]; string title;

            switch (req.Kind)
            {
                case ChangeKind.Launch:
                    if (b.Status != BudgetStatus.Draft) return Results.Problem(statusCode: 409, title: "Only a draft budget can be launched");
                    title = $"Launch budget '{b.Name}': cap {cap:N2} {b.Currency}";
                    break;
                case ChangeKind.Increase:
                    if (b.Status != BudgetStatus.Approved) return Results.Problem(statusCode: 409, title: "Only a launched budget can have its cap raised");
                    if (req.ProposedCap is not { } newCap || newCap <= b.HardCap) return Results.Problem(statusCode: 400, title: "The new cap must be higher than the current cap");
                    if (newCap > BudgetRules.MaxAmount) return Results.Problem(statusCode: 400, title: "The new cap is out of range");
                    cap = newCap; title = $"Raise the cap of '{b.Name}' from {b.HardCap:N2} to {cap:N2} {b.Currency}";
                    break;
                case ChangeKind.Reallocation:
                    if (b.Status != BudgetStatus.Approved) return Results.Problem(statusCode: 409, title: "Only a launched budget can be reallocated");
                    if (req.Allocations is not { Length: > 0 }) return Results.Problem(statusCode: 400, title: "Provide the new allocations");
                    if (BudgetRules.ValidateAllocations(b.HardCap, req.Allocations.Select(a => (a.Label ?? "", a.Amount)).ToList()) is { } ae) return Results.Problem(statusCode: 400, title: ae);
                    foreach (var old in current)
                    {
                        var used = by.GetValueOrDefault(old.Id, SpendTotals.Zero).Exposure;
                        var neu = req.Allocations.FirstOrDefault(a => a.Label.Trim().Equals(old.Label, StringComparison.OrdinalIgnoreCase));
                        if (neu is null && (used > 0 || by.ContainsKey(old.Id))) return Results.Problem(statusCode: 400, title: $"'{old.Label}' already has recorded spend and cannot be removed");
                        if (neu is not null && neu.Amount < used) return Results.Problem(statusCode: 400, title: $"'{old.Label}' has {used:N2} spent or committed, so it cannot be reduced below that");
                    }
                    allocs = req.Allocations; title = $"Reallocate '{b.Name}' ({req.Allocations.Length} lines, total {req.Allocations.Sum(a => a.Amount):N2} of {b.HardCap:N2} {b.Currency})";
                    break;
                default: return Results.Problem(statusCode: 400, title: "Unknown request type");
            }
            if (req.Kind == ChangeKind.Launch && BudgetRules.ValidateAllocations(cap, allocs.Select(a => (a.Label, a.Amount)).ToList()) is { } le) return Results.Problem(statusCode: 400, title: le);

            var change = new BudgetChange
            {
                BudgetId = id, Kind = req.Kind, BaseVersion = b.Version, CurrentCap = b.HardCap, ProposedCap = cap, ProposedAllocationsJson = JsonSerializer.Serialize(allocs, Json),
                Reason = req.Reason.Trim(), Status = ChangeStatus.Pending, RequestedBy = u.UserId, RequestedAt = clock.UtcNow,
            };
            db.Changes.Add(change);
            await db.SaveChangesAsync(ct);
            var ar = await approvals.RequestAsync(new ApprovalRequest(BudgetChangeApprovalHandler.Subject, change.Id.ToString(), 1, b.ProductId, title, req.Reason.Trim()), ct);
            if (!ar.IsSuccess) { db.Changes.Remove(change); await db.SaveChangesAsync(ct); return Results.Problem(statusCode: 400, title: ar.Error!.Message); }
            await audit.WriteAsync(new AuditEntry("budget.change-requested", "Budget", id.ToString(), b.ProductId, title, After: new { change.Kind, change.CurrentCap, change.ProposedCap, change.Reason }), ct);
            return Results.Accepted($"/api/v1/budget/budgets/{id}", new { change.Id, change.Kind, change.Status });
        });

        g.MapPost("/changes/{id:guid}/withdraw", async (Guid id, BudgetDbContext db, ICurrentUser u, IApprovalService approvals, IAuditWriter audit, CancellationToken ct) =>
        {
            var c = await db.Changes.FirstOrDefaultAsync(x => x.Id == id, ct);
            var b = c is null ? null : await db.Budgets.AsNoTracking().FirstAsync(x => x.Id == c.BudgetId, ct);
            if (c is null || b is null || !u.HasPermission(Permissions.BudgetPropose) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            if (c.Status != ChangeStatus.Pending) return Results.Problem(statusCode: 409, title: $"The request is already {c.Status}");
            c.Status = ChangeStatus.Withdrawn;
            await db.SaveChangesAsync(ct);
            await approvals.WithdrawAsync(BudgetChangeApprovalHandler.Subject, c.Id.ToString(), ct);
            await audit.WriteAsync(new AuditEntry("budget.change-withdrawn", "Budget", b.Id.ToString(), b.ProductId, b.Name, After: new { c.Kind }), ct);
            return Results.NoContent();
        });
    }

    // ---------------------------------------------------------------- spend ----------------------------------------------------------------

    private static void MapSpend(RouteGroupBuilder g)
    {
        g.MapGet("/budgets/{id:guid}/spend", async (Guid id, BudgetDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var b = await db.Budgets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.BudgetRead) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            return Results.Ok(await db.Spend.AsNoTracking().Where(s => s.BudgetId == id).OrderByDescending(s => s.OccurredOn).ThenByDescending(s => s.Id).Take(500)
                .Select(s => new { s.Id, s.Kind, s.Amount, s.AllocationId, s.OccurredOn, s.Note, s.Source, s.Voided, s.VoidReason, s.CreatedAt }).ToListAsync(ct));
        });

        g.MapPost("/budgets/{id:guid}/spend", async (Guid id, SpendIn req, BudgetDbContext db, ICurrentUser u, IClock clock, IEventPublisher events, IAuditWriter audit, CancellationToken ct) =>
        {
            var b0 = await db.Budgets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b0 is null || !u.HasPermission(Permissions.BudgetPropose) || !await CanSeeAsync(u, b0, ct)) return Results.NotFound();

            // One writer at a time per budget, so two requests cannot both pass the cap check and together exceed it.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 1))", [id.ToString()], ct);
            var b = await db.Budgets.FirstAsync(x => x.Id == id, ct);
            var (totals, by) = await TotalsAsync(db, id, ct);
            decimal? allocAmount = null; SpendTotals? allocTotals = null;
            if (req.AllocationId is { } aid)
            {
                var a = await db.Allocations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == aid && x.BudgetId == id, ct);
                if (a is null) return Results.Problem(statusCode: 400, title: "That allocation line is not part of this budget");
                allocAmount = a.Amount; allocTotals = by.GetValueOrDefault(aid, SpendTotals.Zero);
            }
            if (BudgetRules.CheckSpend(b.Status, b.HardCap, totals, req.Kind, req.Amount, allocAmount, allocTotals) is { } refusal)
                return Results.Problem(statusCode: 409, title: refusal);

            var now = clock.UtcNow;
            db.Spend.Add(new SpendRecord { BudgetId = id, AllocationId = req.AllocationId, Kind = req.Kind, Amount = Math.Round(req.Amount, 2), OccurredOn = req.OccurredOn ?? DateOnly.FromDateTime(now.UtcDateTime), Note = req.Note is { Length: > 500 } ? req.Note[..500] : req.Note, CreatedBy = u.UserId, CreatedAt = now });
            var newExposure = totals.Exposure + (req.Kind is SpendKind.Committed or SpendKind.Actual ? Math.Round(req.Amount, 2) : 0);
            var level = BudgetRules.Level(newExposure, b.HardCap, b.WarnAtPct, b.StopAtPct);
            var escalated = level > b.LastAlert;
            if (level != b.LastAlert) b.LastAlert = level;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await audit.WriteAsync(new AuditEntry("budget.spend-recorded", "Budget", id.ToString(), b.ProductId, b.Name, After: new { req.Kind, req.Amount, req.AllocationId }), ct);
            if (escalated)
                await events.PublishAsync(new PlatformEvent("budget.threshold-crossed", b.ProductId, "budget", id, new Dictionary<string, string?> { ["level"] = level.ToString() }), ct);
            return Results.Created($"/api/v1/budget/budgets/{id}", new { level });
        });

        g.MapPost("/spend/{id:guid}/void", async (Guid id, VoidIn req, BudgetDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var s0 = await db.Spend.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            var b0 = s0 is null ? null : await db.Budgets.AsNoTracking().FirstAsync(x => x.Id == s0.BudgetId, ct);
            if (s0 is null || b0 is null || !u.HasPermission(Permissions.BudgetPropose) || !await CanSeeAsync(u, b0, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.Problem(statusCode: 400, title: "Say why this entry is being voided");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 1))", [b0.Id.ToString()], ct);
            var s = await db.Spend.FirstAsync(x => x.Id == id, ct);
            if (s.Voided) return Results.NoContent();
            s.Voided = true; s.VoidReason = req.Reason.Trim();   // entries are never deleted: the history stays, it just stops counting
            var b = await db.Budgets.FirstAsync(x => x.Id == b0.Id, ct);
            await db.SaveChangesAsync(ct);
            var (t, _) = await TotalsAsync(db, b.Id, ct);
            var level = BudgetRules.Level(t.Exposure, b.HardCap, b.WarnAtPct, b.StopAtPct);
            if (level < b.LastAlert) b.LastAlert = level;   // dropping back below a threshold re-arms its alert
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await audit.WriteAsync(new AuditEntry("budget.spend-voided", "Budget", b.Id.ToString(), b.ProductId, b.Name, After: new { s.Kind, s.Amount, req.Reason }), ct);
            return Results.NoContent();
        });
    }

    // ---------------------------------------------------------------- planner ----------------------------------------------------------------

    private static void MapPlanner(RouteGroupBuilder g)
    {
        g.MapPost("/budgets/{id:guid}/plan", async (Guid id, PlanIn req, BudgetDbContext db, ICurrentUser u, IMetricReader metrics, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var b = await db.Budgets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.BudgetPropose) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            if (b.ProductId is not { } pid) return Results.Problem(statusCode: 400, title: "Plan from a product or campaign budget: the evidence comes from one product's recorded results");
            var metric = (req.ResultMetric ?? "leads").Trim().ToLowerInvariant();
            if (!MetricRx().IsMatch(metric)) return Results.Problem(statusCode: 400, title: "Unknown result metric");

            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            var points = await metrics.QueryAsync(pid, ["spend", metric], today.AddDays(-26 * 7), today, ct);
            var unusable = 0; var excluded = new HashSet<string>();
            var spend = new Dictionary<(string, DateOnly), decimal>(); var results = new Dictionary<(string, DateOnly), decimal?>();
            foreach (var p in points)
            {
                if (p.PeriodEnd.DayNumber - p.PeriodStart.DayNumber > 7) { unusable++; continue; }
                var key = (p.Channel ?? "", BudgetPlanner.WeekOf(p.PeriodStart));
                if (p.MetricKey == "spend")
                {
                    if (!string.Equals(p.Currency, b.Currency, StringComparison.OrdinalIgnoreCase)) { excluded.Add(p.Currency ?? "?"); continue; }
                    if (p.Value is { } sv) spend[key] = spend.GetValueOrDefault(key) + sv;
                }
                else if (p.Value is { } rv) results[key] = (results.GetValueOrDefault(key) ?? 0) + rv;
                else results.TryAdd(key, null);
            }
            var evidence = spend.Select(s => new WeeklyEvidence(s.Key.Item1, s.Key.Item2, s.Value, results.GetValueOrDefault(s.Key))).ToList();
            var (totals, _) = await TotalsAsync(db, id, ct);
            var labels = await db.Allocations.AsNoTracking().Where(a => a.BudgetId == id).OrderBy(a => a.SortOrder).Select(a => a.Label).ToListAsync(ct);

            var input = new PlannerInput(b.HardCap, totals.Exposure, b.Currency, b.Risk, today, b.PeriodEnd > today ? b.PeriodEnd : today.AddDays(28), metric, evidence, labels, unusable, [.. excluded]);
            var output = BudgetPlanner.Plan(input);
            var plan = new BudgetPlan { BudgetId = id, Mode = output.Mode, InputsJson = JsonSerializer.Serialize(new { metric, weeksOfEvidence = evidence.Count, input.Currency, from = today.AddDays(-26 * 7), to = today }, Json), OutputJson = JsonSerializer.Serialize(output, Json), CreatedBy = u.UserId, CreatedAt = clock.UtcNow };
            db.Plans.Add(plan);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("budget.plan-generated", "Budget", id.ToString(), b.ProductId, b.Name, After: new { output.Mode, metric }), ct);
            return Results.Ok(new { planId = plan.Id, output });
        });

        g.MapGet("/budgets/{id:guid}/plans", async (Guid id, BudgetDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var b = await db.Budgets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.BudgetRead) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            var rows = await db.Plans.AsNoTracking().Where(p => p.BudgetId == id).OrderByDescending(p => p.CreatedAt).Take(20).ToListAsync(ct);
            return Results.Ok(rows.Select(p => new { planId = p.Id, p.Mode, p.CreatedAt, output = JsonSerializer.Deserialize<PlanOutput>(p.OutputJson, Json) }));
        });

        // Adopting a scenario records its lines as Recommended figures only. They cost nothing and change no cap: a person still launches or reallocates through approval.
        g.MapPost("/plans/{id:guid}/adopt", async (Guid id, AdoptIn req, BudgetDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            var b = plan is null ? null : await db.Budgets.AsNoTracking().FirstAsync(x => x.Id == plan.BudgetId, ct);
            if (plan is null || b is null || !u.HasPermission(Permissions.BudgetPropose) || !await CanSeeAsync(u, b, ct)) return Results.NotFound();
            var output = JsonSerializer.Deserialize<PlanOutput>(plan.OutputJson, Json)!;
            if (output.Mode != "Forecast") return Results.Problem(statusCode: 409, title: "This plan is a learning experiment, not a forecast, so there is no scenario to adopt");
            var scenario = output.Scenarios.FirstOrDefault(s => s.Name.Equals(req.Scenario, StringComparison.OrdinalIgnoreCase));
            if (scenario is null) return Results.Problem(statusCode: 400, title: "Unknown scenario");
            var allocs = await db.Allocations.AsNoTracking().Where(a => a.BudgetId == b.Id).ToListAsync(ct);
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            foreach (var c in scenario.Channels)
                db.Spend.Add(new SpendRecord { BudgetId = b.Id, AllocationId = allocs.FirstOrDefault(a => a.Label.Equals(c.Channel, StringComparison.OrdinalIgnoreCase))?.Id, Kind = SpendKind.Recommended, Amount = c.Spend,
                    OccurredOn = today, Note = $"Planner: {scenario.Name} scenario, {c.Basis}", Source = "planner", CreatedBy = u.UserId, CreatedAt = clock.UtcNow });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("budget.plan-adopted", "Budget", b.Id.ToString(), b.ProductId, b.Name, After: new { scenario.Name, Lines = scenario.Channels.Count }), ct);
            return Results.Ok(new { recorded = scenario.Channels.Count });
        });
    }
}

/// <summary>Applies a decision on a budget change. Idempotent; refuses if the budget has moved on since the request was made, so the approval always covers exactly what was asked.</summary>
public sealed class BudgetChangeApprovalHandler(BudgetDbContext db, IClock clock) : IApprovalSubjectHandler
{
    public const string Subject = "BudgetChange";
    public string SubjectType => Subject;
    public string RequiredPermission => Permissions.BudgetApprove;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result> OnDecidedAsync(ApprovalDecisionContext c, CancellationToken ct)
    {
        if (!Guid.TryParse(c.SubjectId, out var id)) return new Error("budget.bad_subject", "Invalid subject id", ErrorKind.Validation);
        var change = await db.Changes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (change is null) return new Error("budget.not_found", "Request not found", ErrorKind.NotFound);

        var wantApplied = c.Decision == ApprovalDecision.Approved;
        if (change.Status == ChangeStatus.Applied && wantApplied) return Result.Ok();                        // replay
        if (change.Status == ChangeStatus.Rejected && !wantApplied) return Result.Ok();
        if (change.Status != ChangeStatus.Pending) return new Error("budget.stale", $"This request is already {change.Status}.", ErrorKind.Conflict);

        var now = clock.UtcNow;
        if (!wantApplied)
        {
            change.Status = ChangeStatus.Rejected; change.DecidedBy = c.DecidedBy; change.DecidedAt = now; change.DecisionNote = c.Comment;
            await db.SaveChangesAsync(ct);
            return Result.Ok();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 1))", [change.BudgetId.ToString()], ct);
        var b = await db.Budgets.FirstAsync(x => x.Id == change.BudgetId, ct);
        if (b.Version != change.BaseVersion) return new Error("budget.stale", "The budget changed after this request was made. Ask for a new request.", ErrorKind.Conflict);
        var proposed = JsonSerializer.Deserialize<BudgetModule.AllocIn[]>(change.ProposedAllocationsJson, Json) ?? [];

        switch (change.Kind)
        {
            case ChangeKind.Launch:
                if (b.Status != BudgetStatus.Draft) return new Error("budget.stale", "The budget is no longer a draft.", ErrorKind.Conflict);
                b.Status = BudgetStatus.Approved; b.HardCap = change.ProposedCap;
                break;
            case ChangeKind.Increase:
                if (change.ProposedCap <= b.HardCap) return new Error("budget.stale", "The cap is already at or above the requested amount.", ErrorKind.Conflict);
                b.HardCap = change.ProposedCap;
                break;
            case ChangeKind.Reallocation:
                var existing = await db.Allocations.Where(a => a.BudgetId == b.Id).ToListAsync(ct);
                var spendRows = await db.Spend.AsNoTracking().Where(s => s.BudgetId == b.Id && !s.Voided && s.AllocationId != null).Select(s => new { s.AllocationId, s.Kind, s.Amount }).ToListAsync(ct);
                var by = spendRows.GroupBy(r => r.AllocationId!.Value).ToDictionary(g => g.Key, g => SpendTotals.From(g.Select(r => (r.Kind, r.Amount))));
                var order = 0;
                foreach (var p in proposed)
                {
                    var match = existing.FirstOrDefault(a => a.Label.Equals(p.Label.Trim(), StringComparison.OrdinalIgnoreCase));
                    var used = match is null ? 0 : by.GetValueOrDefault(match.Id, SpendTotals.Zero).Exposure;
                    if (p.Amount < used) return new Error("budget.stale", $"'{p.Label}' now has more spent or committed than the requested amount.", ErrorKind.Conflict);
                    if (match is null) db.Allocations.Add(new BudgetAllocation { BudgetId = b.Id, Label = p.Label.Trim(), Amount = p.Amount, SortOrder = order++ });
                    else { match.Amount = p.Amount; match.SortOrder = order++; }
                }
                foreach (var gone in existing.Where(a => !proposed.Any(p => p.Label.Trim().Equals(a.Label, StringComparison.OrdinalIgnoreCase))))
                {
                    if (by.ContainsKey(gone.Id)) return new Error("budget.stale", $"'{gone.Label}' now has recorded spend and cannot be removed.", ErrorKind.Conflict);
                    db.Allocations.Remove(gone);
                }
                break;
        }
        if (change.Kind == ChangeKind.Launch)   // the draft's allocation list is replaced by the approved snapshot
        {
            db.Allocations.RemoveRange(db.Allocations.Where(a => a.BudgetId == b.Id));
            var o = 0; foreach (var p in proposed) db.Allocations.Add(new BudgetAllocation { BudgetId = b.Id, Label = p.Label.Trim(), Amount = p.Amount, SortOrder = o++ });
        }
        b.Version++;
        change.Status = ChangeStatus.Applied; change.DecidedBy = c.DecidedBy; change.DecidedAt = now; change.DecisionNote = c.Comment;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Result.Ok();
    }
}

/// <summary>What workflows may know about a budget when a threshold is crossed. The creator is reported as <c>ownerUserId</c>.</summary>
public sealed class BudgetStateProvider(BudgetDbContext db) : ISubjectStateProvider
{
    public string SubjectType => "budget";

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Budgets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return null;
        var rows = await db.Spend.AsNoTracking().Where(s => s.BudgetId == id && !s.Voided).Select(s => new { s.Kind, s.Amount }).ToListAsync(ct);
        var t = SpendTotals.From(rows.Select(r => (r.Kind, r.Amount)));
        return new Dictionary<string, object?>
        {
            ["name"] = b.Name, ["currency"] = b.Currency, ["cap"] = b.HardCap, ["spent"] = t.Actual, ["committed"] = t.Committed,
            ["spentPct"] = b.HardCap > 0 ? Math.Round(t.Exposure * 100m / b.HardCap, 1) : 0m, ["level"] = BudgetRules.Level(t.Exposure, b.HardCap, b.WarnAtPct, b.StopAtPct).ToString(), ["status"] = b.Status.ToString(), ["ownerUserId"] = b.CreatedBy,
        };
    }
}
