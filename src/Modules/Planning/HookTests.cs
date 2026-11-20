using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Planning;

public sealed record TestVariant(string Label, string HookType);

/// <summary>What one variant has achieved so far. Averages cover only posts whose figures were reported; an unreported figure is never counted as zero.</summary>
public sealed record VariantFigures(string Label, string HookType, int Planned, int Published, int Measured, decimal? AvgEngagements, decimal? AvgViews);

/// <summary>Status is NotEnoughPosts, NoClearWinner or Winner.</summary>
public sealed record HookTestVerdict(string Status, string? Winner, string Summary);

public static class HookTestLogic
{
    public const int MinVariants = 2, MaxVariants = 3, MinPostsPerVariant = 3, MaxPostsPerVariant = 5;
    /// <summary>A variant must have this many posts with reported figures before it can be judged.</summary>
    public const int MinMeasured = 3;
    /// <summary>The leader must beat the runner-up by at least this fraction and by at least this many engagements per post.</summary>
    public const decimal MinRelativeGap = 0.25m, MinAbsoluteGap = 2m;

    public static readonly string[] Labels = ["A", "B", "C"];

    public static List<string> Validate(IReadOnlyList<string> hookTypes, int postsPerVariant, string name, string brief)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) e.Add("A name of up to 200 characters is required.");
        if (string.IsNullOrWhiteSpace(brief) || brief.Length > 2000) e.Add("The message to test (up to 2000 characters) is required.");
        if (hookTypes.Count is < MinVariants or > MaxVariants) e.Add($"A test compares {MinVariants} or {MaxVariants} variants.");
        if (hookTypes.Any(h => !HookTypes.All.Contains(h))) e.Add("Every variant needs a hook type from the list.");
        if (hookTypes.Distinct().Count() != hookTypes.Count) e.Add("Each variant must open in a different way (a different hook type).");
        if (postsPerVariant is < MinPostsPerVariant or > MaxPostsPerVariant) e.Add($"Each variant is posted {MinPostsPerVariant} to {MaxPostsPerVariant} times.");
        return e;
    }

    /// <summary>
    /// Posting order: the variants take turns, one post per slot, each slot on its own day (A, B, C, A, B, C ...). Taking turns spreads weekday and time effects across the variants,
    /// and no two posts of the test ever share a day.
    /// </summary>
    public static List<(int Index, string Label, string HookType, DateOnly Day)> Layout(IReadOnlyList<string> hookTypes, int postsPerVariant, DateOnly start, int everyDays)
        => [.. Enumerable.Range(0, hookTypes.Count * postsPerVariant).Select(i => (i, Labels[i % hookTypes.Count], hookTypes[i % hookTypes.Count], start.AddDays(i * Math.Max(1, everyDays))))];

    private static string N(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public static HookTestVerdict Evaluate(IReadOnlyList<VariantFigures> v)
    {
        var short1 = v.Where(x => x.Measured < MinMeasured).ToList();
        if (short1.Count > 0)
            return new("NotEnoughPosts", null, "Not enough measured posts yet: " + string.Join(", ", short1.Select(x => $"variant {x.Label} ({x.HookType}) has {x.Measured} of {MinMeasured}")) + ".");

        var ranked = v.OrderByDescending(x => x.AvgEngagements ?? 0).ToList();
        var top = ranked[0]; var next = ranked[1];
        var gap = (top.AvgEngagements ?? 0) - (next.AvgEngagements ?? 0);
        var clear = gap >= MinAbsoluteGap && (top.AvgEngagements ?? 0) >= (next.AvgEngagements ?? 0) * (1 + MinRelativeGap);
        var figures = string.Join(", ", ranked.Select(x => $"{x.Label} ({x.HookType}) {N(x.AvgEngagements ?? 0)}"));
        return clear
            ? new("Winner", top.Label, $"Variant {top.Label} ({top.HookType}) led with {N(top.AvgEngagements ?? 0)} engagements per post against {N(next.AvgEngagements ?? 0)} for the next best, over at least {MinMeasured} posts each. Averages: {figures}. This is an indication from a small sample, not proof.")
            : new("NoClearWinner", null, $"No clear winner: the averages were too close to call (engagements per post: {figures}). Over at least {MinMeasured} posts each, no variant led by {N(MinAbsoluteGap)} and {(int)(MinRelativeGap * 100)}%. Treat the hook types as equally good for now.");
    }
}

/// <summary>Creates tests as plan slots, judges them from the network's figures, and records the outcome.</summary>
public sealed class HookTestService(PlanningDbContext db, IContentPlanner content, IContentCatalog catalog, IPostPerformanceReader perf, IClock clock, IAuditWriter audit)
{
    public async Task<Op<Guid>> CreateAsync(Guid planId, string name, string brief, IReadOnlyList<string> hookTypes, int postsPerVariant, Guid? actor, CancellationToken ct)
    {
        var errors = HookTestLogic.Validate(hookTypes, postsPerVariant, name, brief);
        if (errors.Count > 0) return Op<Guid>.Fail(new OpError(400, "The test is not valid.", errors));
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null) return Op<Guid>.Fail(new OpError(404, "Plan not found"));
        if (plan.Status is PlanStatus.Cancelled or PlanStatus.Completed) return Op<Guid>.Fail(new OpError(409, $"A {plan.Status.ToString().ToLowerInvariant()} plan cannot be changed."));

        var slots = await db.Slots.AsNoTracking().Where(s => s.PlanId == planId).ToListAsync(ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var lastDay = slots.Count == 0 ? today : DateOnly.FromDateTime(slots.Max(s => s.ScheduledFor).UtcDateTime);
        var start = lastDay.AddDays(plan.EveryDays) > today ? lastDay.AddDays(plan.EveryDays) : today.AddDays(1);
        var layout = HookTestLogic.Layout(hookTypes, postsPerVariant, start, plan.EveryDays);
        var variants = layout.Take(hookTypes.Count).Select(x => new TestVariant(x.Label, x.HookType)).ToList();

        var test = new HookTest
        {
            ProductId = plan.ProductId, PlanId = planId, Name = name.Trim(), Channel = plan.Channel, Brief = brief.Trim(), VariantsJson = JsonSerializer.Serialize(variants, PlanLogic.Json),
            PostsPerVariant = postsPerVariant, Status = HookTestStatus.Running, CreatedBy = actor, CreatedAt = clock.UtcNow,
        };
        db.HookTests.Add(test);
        var pos = slots.Count == 0 ? 0 : slots.Max(s => s.Position);
        foreach (var x in layout)
            db.Slots.Add(new PlanSlot
            {
                PlanId = planId, Position = ++pos, ScheduledFor = new DateTimeOffset(x.Day.ToDateTime(plan.PostTimeUtc), TimeSpan.Zero), Status = SlotStatus.Planned, TestId = test.Id, Variant = x.Label, HookType = x.HookType,
                Angle = $"{test.Name} (variant {x.Label})",
                Brief = $"{test.Brief} Open this post with a {x.HookType} hook. Write it fresh in its own words; do not copy the wording of the other variants.",
            });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("hooktest.created", "HookTest", test.Id.ToString(), plan.ProductId, test.Name, After: new { planId, variants, postsPerVariant, slots = layout.Count }), ct);
        return Op<Guid>.Of(test.Id);
    }

    public sealed record Evaluation(HookTest Test, IReadOnlyList<VariantFigures> Variants, HookTestVerdict Verdict);

    public async Task<Evaluation?> EvaluateAsync(Guid testId, CancellationToken ct)
    {
        var test = await db.HookTests.AsNoTracking().FirstOrDefaultAsync(t => t.Id == testId, ct);
        if (test is null) return null;
        var variants = JsonSerializer.Deserialize<List<TestVariant>>(test.VariantsJson, PlanLogic.Json) ?? [];
        var slots = await db.Slots.AsNoTracking().Where(s => s.TestId == testId).ToListAsync(ct);
        var ids = slots.Where(s => s.ContentItemId != null).Select(s => s.ContentItemId!.Value).ToList();
        var figures = (await perf.GetAsync(ids, ct)).ToDictionary(p => p.ContentItemId);
        var rows = variants.Select(v =>
        {
            var mine = slots.Where(s => s.Variant == v.Label).ToList();
            var perPost = mine.Where(s => s.ContentItemId is not null && figures.ContainsKey(s.ContentItemId.Value)).Select(s => figures[s.ContentItemId!.Value]).ToList();
            var eng = perPost.Select(Leaderboard.Engagements).Where(e => e is not null).Select(e => (decimal)e!.Value).ToList();
            var views = perPost.Where(p => p.Views is not null).Select(p => (decimal)p.Views!.Value).ToList();
            return new VariantFigures(v.Label, v.HookType, mine.Count, perPost.Count, eng.Count, eng.Count == 0 ? null : Math.Round(eng.Average(), 1), views.Count == 0 ? null : Math.Round(views.Average(), 1));
        }).ToList();
        return new Evaluation(test, rows, HookTestLogic.Evaluate(rows));
    }

    /// <summary>Judges every running test of the product and records the ones that have a verdict (a winner or "no clear winner").</summary>
    public async Task<int> ConcludeFinishedAsync(Guid productId, CancellationToken ct)
    {
        var concluded = 0;
        foreach (var id in await db.HookTests.AsNoTracking().Where(t => t.ProductId == productId && t.Status == HookTestStatus.Running).Select(t => t.Id).ToListAsync(ct))
        {
            var e = await EvaluateAsync(id, ct);
            if (e is null || e.Verdict.Status == "NotEnoughPosts") continue;
            var result = JsonSerializer.Serialize(new { e.Verdict.Status, e.Verdict.Winner, e.Verdict.Summary, variants = e.Variants }, PlanLogic.Json);
            await db.HookTests.Where(t => t.Id == id && t.Status == HookTestStatus.Running).ExecuteUpdateAsync(x => x.SetProperty(t => t.Status, HookTestStatus.Concluded).SetProperty(t => t.ResultJson, result).SetProperty(t => t.ConcludedAt, clock.UtcNow), ct);
            await audit.WriteAsync(new AuditEntry("hooktest.concluded", "HookTest", id.ToString(), productId, e.Test.Name, After: new { e.Verdict.Status, e.Verdict.Winner }), ct);
            concluded++;
        }
        return concluded;
    }

    /// <summary>The concluded tests as text for the planner. These sentences and their figures are the only test results it may quote.</summary>
    public async Task<IReadOnlyList<string>> ConcludedSummariesAsync(Guid productId, CancellationToken ct)
    {
        var rows = await db.HookTests.AsNoTracking().Where(t => t.ProductId == productId && t.Status == HookTestStatus.Concluded).OrderByDescending(t => t.ConcludedAt).Take(5).ToListAsync(ct);
        return [.. rows.Select(t => $"Hook test \"{t.Name}\": " + (JsonSerializer.Deserialize<JsonElement>(t.ResultJson).TryGetProperty("summary", out var s) ? s.GetString() : ""))];
    }

    /// <summary>True when a post of the same test is already scheduled or published on that UTC day, which must never happen.</summary>
    public async Task<bool> SiblingOnDayAsync(Guid testId, Guid thisItemId, DateOnly day, CancellationToken ct)
    {
        var ids = await db.Slots.AsNoTracking().Where(s => s.TestId == testId && s.ContentItemId != null && s.ContentItemId != thisItemId).Select(s => s.ContentItemId!.Value).ToListAsync(ct);
        if (ids.Count == 0) return false;
        foreach (var s in await content.GetStatusesAsync(ids, ct)) if (s.Status == "Scheduled" && s.ScheduledFor is { } at && DateOnly.FromDateTime(at.UtcDateTime) == day) return true;
        var productId = await db.HookTests.AsNoTracking().Where(t => t.Id == testId).Select(t => t.ProductId).FirstAsync(ct);
        var published = await catalog.GetPublishedAsync(productId, null, ct);
        return published.Any(p => ids.Contains(p.ItemId) && DateOnly.FromDateTime(p.PublishedAt.UtcDateTime) == day);
    }
}

public static class HookTestEndpoints
{
    public sealed record CreateRequest(string? Name, string? Brief, List<string>? HookTypes, int? PostsPerVariant);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/plans/{planId:guid}/hook-tests", async (Guid planId, CreateRequest req, PlanningDbContext db, ICurrentUser u, HookTestService svc, CancellationToken ct) =>
        {
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
            if (plan is null || !u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            var r = await svc.CreateAsync(planId, req.Name?.Trim() ?? "", req.Brief?.Trim() ?? "", req.HookTypes ?? [], req.PostsPerVariant ?? 3, u.UserId, ct);
            if (r.Ok) return Results.Created($"/api/v1/hook-tests/{r.Value}", new { Id = r.Value });
            return r.Error!.Errors is { Count: > 0 } ? Results.ValidationProblem(new Dictionary<string, string[]> { ["test"] = [.. r.Error.Errors] }) : Results.Problem(statusCode: r.Error.Status, title: r.Error.Title);
        });

        // Records the outcome of every test of the product that already has a verdict (the autopilot does the same on its own).
        g.MapPost("/products/{productId:guid}/hook-tests/conclude", async (Guid productId, ICurrentUser u, HookTestService svc, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            return Results.Ok(new { concluded = await svc.ConcludeFinishedAsync(productId, ct) });
        });

        g.MapGet("/products/{productId:guid}/hook-tests", async (Guid productId, PlanningDbContext db, ICurrentUser u, HookTestService svc, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var ids = await db.HookTests.AsNoTracking().Where(t => t.ProductId == productId).OrderByDescending(t => t.CreatedAt).Take(30).Select(t => t.Id).ToListAsync(ct);
            var list = new List<object>();
            foreach (var id in ids)
            {
                var e = (await svc.EvaluateAsync(id, ct))!;
                list.Add(new { e.Test.Id, e.Test.PlanId, e.Test.Name, e.Test.Channel, status = e.Test.Status.ToString(), e.Test.PostsPerVariant, e.Test.CreatedAt, e.Test.ConcludedAt, minMeasured = HookTestLogic.MinMeasured, variants = e.Variants, verdict = e.Verdict });
            }
            return Results.Ok(list);
        });
    }
}
