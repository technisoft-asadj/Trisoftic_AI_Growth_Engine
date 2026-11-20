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

namespace TriSoftic.GrowthEngine.Modules.Planning;

/// <summary>
/// Growth plans: plan a run of posts, draft them, schedule the approved ones, then review the results and carry the learnings into the next plan.
/// The plan organises work; it never publishes anything and never skips an approval. Every AI step is grounded in the approved product profile.
/// </summary>
public static class PlanningModule
{
    public static IServiceCollection AddPlanningModule(this IServiceCollection s, IConfiguration c) => s.AddModuleDbContext<PlanningDbContext>(c, PlanningDbContext.Schema).AddScoped<PlanOps>().AddScoped<HookTestService>().AddAutopilot(c);

    public sealed record CreatePlanRequest(string Name, string Goal, DateOnly StartDate, int Days, int? EveryDays, string? TimeUtc, string? Channel, string? Language, Guid? FromReviewId);

    private static readonly Regex Placeholder = new(@"\[[^\]\r\n]{2,60}\]", RegexOptions.Compiled);
    private const int AiAttempts = 2;

    private static IResult AiFailure(AiException ex)
        => Results.Problem(statusCode: ex.Outcome switch { AiOutcome.BudgetBlocked => 402, AiOutcome.Paused => 503, AiOutcome.Refused => 422, _ => 502 }, title: ex.Message,
            extensions: new Dictionary<string, object?> { ["outcome"] = ex.Outcome.ToString() });

    private static IResult Problem(OpError e)
        => e.Errors is { Count: > 0 } ? Results.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [.. e.Errors] })
         : Results.Problem(statusCode: e.Status, title: e.Title, extensions: e.Outcome is null && e.Code is null ? null : new Dictionary<string, object?> { ["outcome"] = e.Outcome, ["code"] = e.Code });

    private static IResult Fail(Error e) => Results.Problem(statusCode: e.Kind switch { ErrorKind.Conflict => 409, ErrorKind.Policy => 422, _ => 400 }, title: e.Message, extensions: new Dictionary<string, object?> { ["code"] = e.Code });

    public static IEndpointRouteBuilder MapPlanningEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Planning");

        g.MapGet("/products/{productId:guid}/plans", async (Guid productId, PlanningDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var plans = await db.Plans.AsNoTracking().Where(p => p.ProductId == productId).OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
            var ids = plans.Select(p => p.Id).ToList();
            var counts = await db.Slots.AsNoTracking().Where(s => ids.Contains(s.PlanId)).GroupBy(s => s.PlanId)
                .Select(x => new { PlanId = x.Key, Slots = x.Count(), Drafted = x.Count(s => s.Status == SlotStatus.Drafted) }).ToDictionaryAsync(x => x.PlanId, ct);
            return Results.Ok(plans.Select(p => new { p.Id, p.Name, p.Goal, p.Channel, p.StartDate, p.Days, p.Status, p.CreatedAt, slots = counts.GetValueOrDefault(p.Id)?.Slots ?? 0, drafted = counts.GetValueOrDefault(p.Id)?.Drafted ?? 0 }));
        });

        g.MapPost("/products/{productId:guid}/plans", async (Guid productId, CreatePlanRequest req, PlanOps ops, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var time = TimeOnly.TryParse(req.TimeUtc ?? "13:00", out var t) ? t : new TimeOnly(13, 0);
            var spec = new PlanSpec(req.Name?.Trim() ?? "", req.Goal?.Trim() ?? "", string.IsNullOrWhiteSpace(req.Channel) ? "Facebook" : req.Channel.Trim(), string.IsNullOrWhiteSpace(req.Language) ? null : req.Language.Trim(),
                req.StartDate, req.Days, req.EveryDays ?? 1, time);
            var r = await ops.CreatePlanAsync(productId, spec, req.FromReviewId, managed: false, u.UserId, ct);
            return r.Ok ? Results.Created($"/api/v1/plans/{r.Value}", new { Id = r.Value }) : Problem(r.Error!);
        });

        LeaderboardEndpoint.Map(g);

        g.MapGet("/plans/{id:guid}", async (Guid id, PlanningDbContext db, ICurrentUser u, IContentPlanner content, IPostPerformanceReader perf, CancellationToken ct) =>
        {
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            var slots = await db.Slots.AsNoTracking().Where(s => s.PlanId == id).OrderBy(s => s.Position).ToListAsync(ct);
            var itemIds = slots.Where(s => s.ContentItemId != null).Select(s => s.ContentItemId!.Value).ToList();
            var statuses = (await content.GetStatusesAsync(itemIds, ct)).ToDictionary(x => x.ItemId);
            var perfs = (await perf.GetAsync(itemIds, ct)).ToDictionary(x => x.ContentItemId);
            var reviews = await db.Reviews.AsNoTracking().Where(r => r.PlanId == id).OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
            return Results.Ok(new
            {
                plan.Id, plan.ProductId, plan.Name, plan.Goal, plan.Channel, plan.Language, plan.StartDate, plan.Days, plan.EveryDays, postTimeUtc = plan.PostTimeUtc.ToString("HH:mm"), plan.Status, plan.Rationale,
                measures = JsonSerializer.Deserialize<JsonElement>(plan.MeasuresJson), plan.PreviousPlanId, plan.PreviousReviewId, plan.Provider, plan.Model, plan.CostUsd, plan.CreatedAt,
                slots = slots.Select(s =>
                {
                    statuses.TryGetValue(s.ContentItemId ?? Guid.Empty, out var c); perfs.TryGetValue(s.ContentItemId ?? Guid.Empty, out var p);
                    return new
                    {
                        s.Id, s.Position, s.ScheduledFor, s.Angle, s.Brief, s.HookType, s.TestId, s.Variant, s.Visual, s.ImageBrief, s.Status, s.ContentItemId,
                        content = c is null ? null : new { c.Title, c.Status, c.ScheduledFor, approved = c.ApprovedVersion == c.CurrentVersion },
                        results = p is null ? null : new { p.PublishedAt, p.Url, p.MeasuredAt, p.Reactions, p.Comments, p.Shares, p.Views, p.Reach, p.Clicks, p.Note },
                    };
                }),
                reviews = reviews.Select(r => new
                {
                    r.Id, r.CreatedAt, r.Provider, r.Model, r.CostUsd, dataQuality = JsonSerializer.Deserialize<JsonElement>(r.DataQualityJson), result = JsonSerializer.Deserialize<JsonElement>(r.ResultJson),
                }),
            });
        });

        // Writes ONE slot's post from the approved profile, creates it as ordinary content and submits it for approval. One slot per call so each request stays short.
        g.MapPost("/plans/{id:guid}/slots/{slotId:guid}/draft", async (Guid id, Guid slotId, PlanningDbContext db, ICurrentUser u, PlanOps ops, CancellationToken ct) =>
        {
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.ContentWrite) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            var r = await ops.DraftSlotAsync(id, slotId, ct);
            return r.Ok ? Results.Ok(new { slotId, contentItemId = r.Value!.ContentItemId, submitted = r.Value.Submitted, submitError = r.Value.SubmitError, questions = r.Value.Questions }) : Problem(r.Error!);
        });

        // Schedules every post of the plan that has been approved, at its planned time. Posts still awaiting approval are left alone and reported.
        g.MapPost("/plans/{id:guid}/schedule", async (Guid id, PlanningDbContext db, ICurrentUser u, PlanOps ops, CancellationToken ct) =>
        {
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            var r = await ops.SchedulePlanAsync(id, autopilot: false, maxPerDay: int.MaxValue, ct);
            return r.Ok ? Results.Ok(new { scheduled = r.Value!.Scheduled, skipped = r.Value.Skipped.Select(s => new { s.Position, reason = s.Reason }) }) : Problem(r.Error!);
        });

        g.MapPost("/plans/{id:guid}/status/{status}", async (Guid id, string status, PlanningDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            if (!Enum.TryParse<PlanStatus>(status, true, out var next) || next is not (PlanStatus.Completed or PlanStatus.Cancelled)) return Results.Problem(statusCode: 400, title: "A plan can be marked Completed or Cancelled.");
            if (plan.Status is PlanStatus.Cancelled or PlanStatus.Completed) return Results.Problem(statusCode: 409, title: $"The plan is already {plan.Status.ToString().ToLowerInvariant()}.");
            plan.Status = next; plan.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("plan." + next.ToString().ToLowerInvariant(), "GrowthPlan", id.ToString(), plan.ProductId, plan.Name), ct);
            return Results.NoContent();
        });

        // Looks at the plan's published posts and their reported figures, says what they show, and proposes the next plan. With nothing to analyse it says so and does not call the model.
        g.MapPost("/plans/{id:guid}/review", async (Guid id, PlanningDbContext db, ICurrentUser u, PlanOps ops, CancellationToken ct) =>
        {
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.CampaignsManage) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            var r = await ops.ReviewPlanAsync(id, u.UserId, ct);
            return r.Ok ? Results.Created($"/api/v1/plans/{id}", new { Id = r.Value }) : Problem(r.Error!);
        });

        AutopilotEndpoints.Map(g);
        HookTestEndpoints.Map(g);

        return app;
    }
}

/// <summary>What has worked so far, by format, hook type, weekday and time slot. Read-only; it is the same table the planner is given.</summary>
public static class LeaderboardEndpoint
{
    public static async Task<LeaderboardResult> BuildAsync(IContentCatalog catalog, IPostPerformanceReader perf, IConfiguration cfg, Guid productId, string channel, CancellationToken ct)
    {
        var posts = await catalog.GetPublishedAsync(productId, channel, ct);
        var figures = (await perf.GetAsync(posts.Select(p => p.ItemId).ToList(), ct)).ToDictionary(p => p.ContentItemId);
        var rows = posts.Select(p => { figures.TryGetValue(p.ItemId, out var f); return new LeaderboardRow(p.ItemId, p.Format, p.HookType, p.PublishedAt, Leaderboard.Engagements(f), f?.Views, p.Visual); }).ToList();
        return Leaderboard.Build(channel, cfg.GetValue("Planning:Leaderboard:UtcOffsetHours", 0), rows);
    }

    public static void Map(RouteGroupBuilder g)
        => g.MapGet("/products/{productId:guid}/leaderboard", async (Guid productId, string? channel, ICurrentUser u, IContentCatalog catalog, IPostPerformanceReader perf, IConfiguration cfg, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            return Results.Ok(await BuildAsync(catalog, perf, cfg, productId, string.IsNullOrWhiteSpace(channel) ? "Facebook" : channel.Trim(), ct));
        });
}
