using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Planning;

public sealed record AutopilotReport(IReadOnlyList<string> Notes);

/// <summary>
/// Runs a product's growth loop with no one pressing anything: plan the next days, write each post, have the AI reviewer approve or reject it, schedule what passes, and review the results
/// when a plan ends. It only touches plans it created itself. Anything the reviewer is unsure about, or that keeps failing, is left for a person (the "Needs you" list).
/// </summary>
public sealed class AutopilotEngine(PlanningDbContext db, PlanOps ops, IProductKnowledge knowledge, IAiGenerator ai, IContentCatalog catalog, IContentPublishSource source,
    IAgentApprovals agent, HookTestService tests, IAssetCatalog assetCatalog, IImageMaker maker, IPauseSwitch pause, IClock clock, IAuditWriter audit, ITrackedLinks links, InstagramMirrorService mirror)
{
    public const int MaxAttempts = 3;
    private const int MaxDraftsPerRun = 5;
    private const string Subject = "ContentItem";

    public async Task<AutopilotReport> RunAsync(Guid productId, CancellationToken ct)
    {
        var notes = new List<string>();
        var s = await db.Autopilot.FirstOrDefaultAsync(x => x.ProductId == productId, ct);
        if (s is null || !s.Enabled) return new(["Autopilot is off for this product."]);
        try
        {
            if (await pause.IsPausedAsync(PauseScope.Ai, ct)) notes.Add("AI is paused, so nothing was written or reviewed.");
            else if (await pause.IsPausedAsync(PauseScope.Publishing, ct)) notes.Add("Publishing is paused, so the autopilot is holding.");
            else
            {
                var k = await knowledge.GetApprovedAsync(productId, ct);
                if (k is null) notes.Add("The product has no approved profile.");
                else
                {
                    // Each step re-reads the database: an earlier step updates rows directly, and a stale in-memory copy would hide that.
                    if (await tests.ConcludeFinishedAsync(productId, ct) is > 0 and var done) notes.Add($"Concluded {done} hook test(s).");
                    await ReviewFinishedPlansAsync(s, notes, ct); db.ChangeTracker.Clear();
                    await WeeklyReviewsAsync(s, notes, ct); db.ChangeTracker.Clear();
                    await EnsurePlanAsync(s, notes, ct); db.ChangeTracker.Clear();
                    await DraftAsync(s, notes, ct); db.ChangeTracker.Clear();
                    await ReviewPostsAsync(s, k, notes, ct); db.ChangeTracker.Clear();
                    await ScheduleAsync(s, notes, ct); db.ChangeTracker.Clear();
                    await mirror.RunAsync(s, notes, ct);
                }
            }
        }
        catch (AiException ex) { notes.Add($"Stopped: {ex.Message}"); }
        db.ChangeTracker.Clear();
        s = await db.Autopilot.FirstAsync(x => x.ProductId == productId, ct);
        s.LastRunAt = clock.UtcNow; s.LastRunNote = string.Join(" ", notes).Trim() is { Length: > 0 } n ? (n.Length > 900 ? n[..900] : n) : "Ran; nothing needed doing.";
        await db.SaveChangesAsync(ct);
        return new(notes);
    }

    private IQueryable<GrowthPlan> Managed(Guid productId) => db.Plans.Where(p => p.ProductId == productId && p.Managed && p.Status != PlanStatus.Cancelled && p.Status != PlanStatus.Completed);

    // 1. a plan always covers the next few days
    private async Task EnsurePlanAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var live = await Managed(s.ProductId).ToListAsync(ct);
        var coverageEnd = live.Count == 0 ? today : live.Max(p => p.StartDate.AddDays(p.Days));
        if (coverageEnd > today.AddDays(2)) return;

        var start = coverageEnd > today ? coverageEnd : today.AddDays(1);
        // learn from the most recent review of any plan of this product
        var latest = await (from r in db.Reviews join p in db.Plans on r.PlanId equals p.Id where p.ProductId == s.ProductId orderby r.CreatedAt descending select r.Id).FirstOrDefaultAsync(ct);
        var more = ParseTimes(s.ExtraPostTimesUtc);
        var spec = new PlanSpec($"Autopilot {start:yyyy-MM-dd}", s.Goal, s.Channel, s.Language, start, s.PlanDays, s.EveryDays, s.PostTimeUtc, more.Count == 0 ? null : more);
        var videos = s.UseVideos ? await ops.CountFreeVideosAsync(s.ProductId, ct) : 0;
        var visuals = s.UseImages || videos > 0 ? new VisualOptions(s.UseImages, videos) : null;
        var r2 = await ops.CreatePlanAsync(s.ProductId, spec, latest == Guid.Empty ? null : latest, managed: true, actor: null, ct, visuals);
        if (r2.Ok) { notes.Add($"Planned {PlanLogic.SlotCount(s.PlanDays, s.EveryDays, PlanLogic.PerDay(spec))} posts starting {start:yyyy-MM-dd}."); return; }
        notes.Add("Could not plan: " + r2.Error!.Title);
        if (r2.Error.Status is 402 or 503) throw new AiException(r2.Error.Title, r2.Error.Outcome == "Paused" ? AiOutcome.Paused : AiOutcome.BudgetBlocked);
    }

    // 2. write the posts that are coming up
    private async Task DraftAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        var horizon = clock.UtcNow.AddHours(s.LookaheadHours);
        var planIds = await Managed(s.ProductId).Select(p => p.Id).ToListAsync(ct);
        var slots = await db.Slots.Where(x => planIds.Contains(x.PlanId) && x.Status == SlotStatus.Planned && x.Attempts < MaxAttempts && x.ScheduledFor <= horizon).OrderBy(x => x.ScheduledFor).Take(MaxDraftsPerRun).ToListAsync(ct);
        foreach (var slot in slots)
        {
            var r = await ops.DraftSlotAsync(slot.PlanId, slot.Id, ct, new MediaPolicy(s.UseImages, s.UseVideos));
            if (r.Ok) { notes.Add($"Wrote post {slot.Position}." + (r.Value!.MediaNote is { } mn ? " " + mn : "")); continue; }
            if (r.Error!.Status is 402 or 503) throw new AiException(r.Error.Title, r.Error.Outcome == "Paused" ? AiOutcome.Paused : AiOutcome.BudgetBlocked);
            // a draft that fails (for example a placeholder because a fact is missing) counts as an attempt, so it cannot loop and spend forever
            await db.Slots.Where(x => x.Id == slot.Id && x.Status == SlotStatus.Planned).ExecuteUpdateAsync(x => x.SetProperty(y => y.Attempts, y => y.Attempts + 1).SetProperty(y => y.Guidance, Trim(r.Error.Title, 500)), ct);
            await Log(s.ProductId, slot.PlanId, slot.Id, null, 0, "Deferred", slot.Attempts + 1, "Could not write this post: " + r.Error.Title, [], "", 0, ct);
            notes.Add($"Post {slot.Position}: {r.Error.Title}");
        }
    }

    // 3. the AI reviewer decides
    private async Task ReviewPostsAsync(AutopilotSettings s, ApprovedKnowledge k, List<string> notes, CancellationToken ct)
    {
        var planIds = await Managed(s.ProductId).Select(p => p.Id).ToListAsync(ct);
        var slots = await db.Slots.Where(x => planIds.Contains(x.PlanId) && x.Status == SlotStatus.Drafted && x.ContentItemId != null).ToListAsync(ct);
        if (slots.Count == 0) return;
        var bySubject = slots.ToDictionary(x => x.ContentItemId!.Value.ToString());
        var pending = await agent.ListPendingAsync(Subject, [.. bySubject.Keys], ct);
        if (pending.Count == 0) return;
        var recent = await catalog.GetRecentBodiesAsync(s.ProductId, s.Channel, clock.UtcNow.AddDays(-30), ct);
        var already = await db.Decisions.AsNoTracking().Where(d => d.ProductId == s.ProductId && d.Verdict == "Escalated" && d.ContentItemId != null).Select(d => new { d.ContentItemId, d.Version }).ToListAsync(ct);

        foreach (var p in pending)
        {
            var slot = bySubject[p.SubjectId]; var itemId = slot.ContentItemId!.Value;
            if (already.Any(a => a.ContentItemId == itemId && a.Version == p.SubjectVersion)) continue;   // already handed to a person; do not ask again
            var c = await source.GetAsync(itemId, p.SubjectVersion, ct);
            if (c is null || c.Status != "InReview" || c.CurrentVersion != p.SubjectVersion) continue;
            var others = recent.Where(b => b != c.Body).ToList();

            var gates = AutopilotGates.Check(c.Body, k, others, await links.OwnShortUrlsAsync(s.ProductId, AutopilotGates.UrlsIn(c.Body), ct));
            var media = c.AssetIds is { Count: > 0 } ids ? await assetCatalog.GetAsync(ids, ct) : [];
            gates.AddRange(AutopilotGates.CheckMedia(c.Format, media, s.ProductId, clock.UtcNow, c.Body, slot.ImageBrief, maker.CheckBrief));
            var mediaNote = media.Count == 0 ? null : string.Join("; ", media.Select(m => m.IsSynthetic
                ? $"an AI-generated cartoon-style illustration (brief: \"{slot.ImageBrief}\"), not a real screenshot"
                : $"the owner's uploaded {(MediaRules.IsVideo(m) ? "vertical video" : "picture")} \"{m.Name}\" (you cannot see it; the text must not claim what it shows)"));
            ReviewerVerdict? verdict = null; string model = ""; decimal cost = 0;
            if (gates.All(g => g.Passed))
            {
                string? err = null;
                for (var attempt = 1; attempt <= 2 && verdict is null; attempt++)
                {
                    var prompt = AutopilotReviewer.BuildUser(c.Channel, slot.HookType, c.Body, mediaNote) + (attempt == 1 ? "" : $"\n\nYour previous answer was unusable ({err}). Return the JSON again.");
                    var res = await ai.GenerateAsync(new AiRequest("autopilot.review", AiTier.Strategy, AutopilotReviewer.BuildSystem(k), prompt, s.ProductId, "autopilot.review", 1, MaxTokens: 1500), ct);
                    cost += res.EstimatedCostUsd; model = res.Model;
                    verdict = AutopilotReviewer.Parse(res.Text, out err);
                }
            }
            var (outcome, reasons) = AutopilotReviewer.Decide(gates, verdict);
            var checks = gates.Select(g => new { g.Name, g.Passed, g.Detail }).Cast<object>().ToList();
            if (verdict is not null) checks.Add(new { Name = "AI reviewer", Passed = outcome == AutopilotOutcome.Approve, Detail = $"{verdict.Verdict}: {verdict.Summary}" });

            if (outcome == AutopilotOutcome.Approve)
            {
                var d = await agent.DecideAsync(p.ApprovalId, ApprovalDecision.Approved, "Approved by the AI reviewer. " + reasons, ct);
                if (!d.IsSuccess) { notes.Add($"Post {slot.Position}: approval failed: {d.Error!.Message}"); continue; }
                await Log(s.ProductId, slot.PlanId, slot.Id, itemId, p.SubjectVersion, "Approved", slot.Attempts, reasons, checks, model, cost, ct);
                notes.Add($"Approved post {slot.Position}.");
            }
            else if (outcome == AutopilotOutcome.Reject)
            {
                var d = await agent.DecideAsync(p.ApprovalId, ApprovalDecision.Rejected, "Rejected by the AI reviewer. " + Trim(reasons, 900), ct);
                if (!d.IsSuccess) { notes.Add($"Post {slot.Position}: rejection failed: {d.Error!.Message}"); continue; }
                if (slot.Attempts < MaxAttempts)
                {
                    // the rejected item ends its life; the slot goes back to be written again, told what to avoid
                    slot.Status = SlotStatus.Planned; slot.ContentItemId = null; slot.Guidance = Trim(reasons, 1500);
                    await Log(s.ProductId, slot.PlanId, slot.Id, itemId, p.SubjectVersion, "Rejected", slot.Attempts, reasons + " It will be rewritten.", checks, model, cost, ct);
                    notes.Add($"Rejected post {slot.Position}; it will be rewritten.");
                }
                else
                {
                    await Log(s.ProductId, slot.PlanId, slot.Id, itemId, p.SubjectVersion, "Escalated", slot.Attempts, $"Rejected {slot.Attempts} times. Last reasons: {reasons}", checks, model, cost, ct);
                    notes.Add($"Post {slot.Position} was rejected {slot.Attempts} times and needs you.");
                }
            }
            else
            {
                await Log(s.ProductId, slot.PlanId, slot.Id, itemId, p.SubjectVersion, "Escalated", slot.Attempts, reasons, checks, model, cost, ct);
                notes.Add($"Post {slot.Position} is left for you: {Trim(reasons, 120)}");
            }
            await db.SaveChangesAsync(ct);
        }
    }

    // 4. schedule what passed
    private async Task ScheduleAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        foreach (var plan in await Managed(s.ProductId).ToListAsync(ct))
        {
            if (!await db.Slots.AnyAsync(x => x.PlanId == plan.Id && x.ContentItemId != null, ct)) continue;
            var r = await ops.SchedulePlanAsync(plan.Id, autopilot: true, s.MaxPostsPerDay, ct);
            if (r.Ok && r.Value!.Scheduled > 0) notes.Add($"Scheduled {r.Value.Scheduled} post(s).");
        }
    }

    // 5. when a plan has ended, look at how it did; the next plan is built from that
    private async Task ReviewFinishedPlansAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-2);
        foreach (var plan in await Managed(s.ProductId).ToListAsync(ct))
        {
            var end = await db.Slots.Where(x => x.PlanId == plan.Id).MaxAsync(x => (DateTimeOffset?)x.ScheduledFor, ct);
            if (end is null || end > cutoff) continue;
            var lastReview = await db.Reviews.Where(r => r.PlanId == plan.Id).MaxAsync(r => (DateTimeOffset?)r.CreatedAt, ct);
            if (lastReview is null || lastReview < end)
            {
                var r = await ops.ReviewPlanAsync(plan.Id, null, ct);
                if (!r.Ok)
                {
                    if (r.Error!.Status is 402 or 503) throw new AiException(r.Error.Title, r.Error.Outcome == "Paused" ? AiOutcome.Paused : AiOutcome.BudgetBlocked);
                    notes.Add("Could not review a finished plan: " + r.Error.Title); continue;
                }
                notes.Add($"Reviewed the plan \"{plan.Name}\".");
            }
            plan.Status = PlanStatus.Completed; plan.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    // 5b. a plan longer than a week is reviewed every week while it runs, so the learnings register keeps moving
    private async Task WeeklyReviewsAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        var now = clock.UtcNow; var today = DateOnly.FromDateTime(now.UtcDateTime);
        foreach (var plan in await Managed(s.ProductId).ToListAsync(ct))
        {
            if (plan.StartDate.AddDays(7) > today) continue;
            var last = await db.Reviews.Where(r => r.PlanId == plan.Id).MaxAsync(r => (DateTimeOffset?)r.CreatedAt, ct);
            if (last is not null && last > now.AddDays(-7)) continue;
            var since = last ?? plan.CreatedAt;
            var fresh = (await catalog.GetPublishedAsync(s.ProductId, s.Channel, ct)).Count(p => p.PublishedAt > since);
            if (fresh < 3) continue;   // too little new to say anything
            var r = await ops.ReviewPlanAsync(plan.Id, null, ct);
            if (r.Ok) { notes.Add($"Weekly review of \"{plan.Name}\" recorded."); continue; }
            if (r.Error!.Status is 402 or 503) throw new AiException(r.Error.Title, r.Error.Outcome == "Paused" ? AiOutcome.Paused : AiOutcome.BudgetBlocked);
            notes.Add("Could not do the weekly review: " + r.Error.Title);
        }
    }

    public static List<TimeOnly> ParseTimes(string? csv) => string.IsNullOrWhiteSpace(csv) ? [] : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => TimeOnly.TryParse(x, out var t) ? t : (TimeOnly?)null).Where(t => t is not null).Select(t => t!.Value)];

    private static string Trim(string v, int n) => v.Length <= n ? v : v[..n];

    private async Task Log(Guid productId, Guid? planId, Guid? slotId, Guid? itemId, int version, string verdict, int attempt, string reasons, IReadOnlyList<object> checks, string model, decimal cost, CancellationToken ct)
    {
        db.Decisions.Add(new AutopilotDecision
        {
            ProductId = productId, PlanId = planId, SlotId = slotId, ContentItemId = itemId, Version = version, Verdict = verdict, Attempt = attempt, Reasons = Trim(reasons, 4000),
            ChecksJson = JsonSerializer.Serialize(checks, PlanLogic.Json), Model = model, CostUsd = cost, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("autopilot." + verdict.ToLowerInvariant(), "ContentItem", itemId?.ToString(), productId, Trim(reasons, 200), After: new { version, attempt, cost }), ct);
    }
}

/// <summary>One run per product at a time, whether the loop or a person pressing "Run now" started it.</summary>
public static class AutopilotLocks
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    public static async Task<bool> RunAsync(Guid productId, Func<Task> work)
    {
        var l = Locks.GetOrAdd(productId, _ => new SemaphoreSlim(1, 1));
        if (!await l.WaitAsync(0)) return false;
        try { await work(); return true; } finally { l.Release(); }
    }
}

/// <summary>Wakes every few minutes and runs the autopilot for each product that has it switched on. Off in tests (they call the endpoint instead).</summary>
public sealed class AutopilotLoop(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<AutopilotLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var every = TimeSpan.FromSeconds(Math.Max(30, cfg.GetValue("Autopilot:IntervalSeconds", 300)));
        try { await Task.Delay(TimeSpan.FromSeconds(45), stop); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(every);
        try
        {
        do
        {
            try
            {
                List<Guid> products;
                using (var scope = scopes.CreateScope()) products = await scope.ServiceProvider.GetRequiredService<PlanningDbContext>().Autopilot.AsNoTracking().Where(x => x.Enabled).Select(x => x.ProductId).ToListAsync(stop);
                foreach (var pid in products)
                {
                    try
                    {
                        await AutopilotLocks.RunAsync(pid, async () =>
                        {
                            using var scope = scopes.CreateScope();
                            await scope.ServiceProvider.GetRequiredService<AutopilotEngine>().RunAsync(pid, stop);
                        });
                        // checking for problems must never stop the autopilot, and a failure here is only logged
                        try
                        {
                            using var scope = scopes.CreateScope();
                            var ops = scope.ServiceProvider.GetRequiredService<OpsService>();
                            await ops.CheckAlertsAsync(pid, stop);
                            await ops.DigestIfDueAsync(pid, stop);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Owner checks failed for product {ProductId}", pid); }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Autopilot run failed for product {ProductId}", pid); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Autopilot loop failed"); }
        }
        while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}

public static class AutopilotRegistration
{
    public static IServiceCollection AddAutopilot(this IServiceCollection s, IConfiguration c)
    {
        s.AddScoped<AutopilotEngine>();
        if (c.GetValue("Autopilot:RunLoop", true)) s.AddHostedService<AutopilotLoop>();
        return s;
    }
}

public static class AutopilotEndpoints
{
    public sealed record SettingsRequest(bool Enabled, string? Channel, string? Language, string? Goal, int? PlanDays, int? EveryDays, string? PostTimeUtc, IReadOnlyList<string>? PostTimesUtc, int? MaxPostsPerDay, int? LookaheadHours, bool? UseImages = null, bool? UseVideos = null, bool? AlsoInstagram = null);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/products/{productId:guid}/autopilot", async (Guid productId, PlanningDbContext db, ICurrentUser u, IContentPlanner content, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var s = await db.Autopilot.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == productId, ct) ?? new AutopilotSettings { ProductId = productId };
            var decisions = await db.Decisions.AsNoTracking().Where(d => d.ProductId == productId).OrderByDescending(d => d.CreatedAt).Take(60).ToListAsync(ct);

            // "needs you": posts the reviewer handed over that are still waiting, and slots that failed too often
            var escalated = decisions.Where(d => d.Verdict == "Escalated" && d.ContentItemId != null).ToList();
            var statuses = (await content.GetStatusesAsync(escalated.Select(d => d.ContentItemId!.Value).Distinct().ToList(), ct)).ToDictionary(x => x.ItemId);
            var needsYou = escalated.Where(d => statuses.TryGetValue(d.ContentItemId!.Value, out var st) && st.Status == "InReview" && st.CurrentVersion == d.Version)
                .Select(d => new { d.ContentItemId, d.Reasons, d.CreatedAt, title = statuses[d.ContentItemId!.Value].Title }).ToList();
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var week = await db.Decisions.AsNoTracking().Where(d => d.ProductId == productId && d.CreatedAt >= since).GroupBy(d => d.Verdict).Select(x => new { verdict = x.Key, n = x.Count(), cost = x.Sum(y => y.CostUsd) }).ToListAsync(ct);
            return Results.Ok(new
            {
                s.Enabled, s.Channel, s.Language, s.Goal, s.PlanDays, s.EveryDays, postTimeUtc = s.PostTimeUtc.ToString("HH:mm"), postTimesUtc = new[] { s.PostTimeUtc }.Concat(AutopilotEngine.ParseTimes(s.ExtraPostTimesUtc)).OrderBy(t => t).Select(t => t.ToString("HH:mm")), s.MaxPostsPerDay, s.LookaheadHours, s.UseImages, s.UseVideos, s.AlsoInstagram, s.LastRunAt, s.LastRunNote,
                needsYou, week,
                decisions = decisions.Select(d => new { d.Id, d.ContentItemId, d.Version, d.Verdict, d.Attempt, d.Reasons, d.CostUsd, d.CreatedAt, checks = JsonSerializer.Deserialize<JsonElement>(d.ChecksJson) }),
            });
        });

        g.MapPut("/products/{productId:guid}/autopilot", async (Guid productId, SettingsRequest req, PlanningDbContext db, ICurrentUser u, IProductKnowledge knowledge, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !u.HasPermission(Permissions.PublishingManage) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var errors = new List<string>();
            var goal = req.Goal?.Trim() ?? "";
            if (goal.Length is < 1 or > 2000) errors.Add("A goal of up to 2000 characters is required.");
            var channel = string.IsNullOrWhiteSpace(req.Channel) ? "Facebook" : req.Channel.Trim();
            if (channel.Length > 60) errors.Add("The channel name is too long.");
            var planDays = req.PlanDays ?? 7; var every = req.EveryDays ?? 1; var cap = req.MaxPostsPerDay ?? 2; var look = req.LookaheadHours ?? 72;
            if (planDays is < 3 or > 31) errors.Add("A plan covers 3 to 31 days.");
            if (every is < 1 or > 7) errors.Add("Posts can be 1 to 7 days apart.");
            if (cap is < 1 or > 5) errors.Add("The daily cap is 1 to 5 posts.");
            if (look is < 24 or > 168) errors.Add("Posts are written 24 to 168 hours ahead.");
            // posting times: the list when given, otherwise the single time; the earliest is the main one
            var rawTimes = req.PostTimesUtc is { Count: > 0 } ? req.PostTimesUtc : [req.PostTimeUtc ?? "13:00"];
            var times = new List<TimeOnly>();
            foreach (var rt in rawTimes) { if (TimeOnly.TryParse(rt, out var tt)) times.Add(tt); else errors.Add($"The posting time {rt} is not valid (HH:mm, UTC)."); }
            times = [.. times.Distinct().OrderBy(t => t)];
            if (times.Count > 0) errors.AddRange(PlanLogic.ValidateTimes(times));
            if (times.Count > 0 && cap < times.Count) errors.Add($"The daily limit ({cap}) is lower than the number of posting times ({times.Count}).");
            if (times.Count > 0 && every is >= 1 and <= 7 && planDays is >= 3 and <= 31 && PlanLogic.SlotCount(planDays, every, times.Count) > PlanLogic.MaxSlots) errors.Add("That is too many posts per plan. Shorten the plan, space the days out or post less often per day.");
            var time = times.Count > 0 ? times[0] : default;
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["autopilot"] = [.. errors] });
            var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language.Trim();
            if (req.Enabled)
            {
                var k = await knowledge.GetApprovedAsync(productId, ct);
                if (k is null) return Results.Problem(statusCode: 409, title: "Approve the product profile before switching the autopilot on.");
                if (language is not null && !k.Markets.SelectMany(m => m.Languages).Contains(language, StringComparer.OrdinalIgnoreCase)) return Results.Problem(statusCode: 400, title: $"Language '{language}' is not a verified market language for this product.");
            }
            var s = await db.Autopilot.FirstOrDefaultAsync(x => x.ProductId == productId, ct);
            var was = s?.Enabled ?? false;
            if (s is null) { s = new AutopilotSettings { ProductId = productId }; db.Autopilot.Add(s); }
            s.Enabled = req.Enabled; s.Channel = channel; s.Language = language; s.Goal = goal; s.PlanDays = planDays; s.EveryDays = every; s.PostTimeUtc = time; s.ExtraPostTimesUtc = string.Join(",", times.Skip(1).Select(t => t.ToString("HH:mm"))); s.MaxPostsPerDay = cap; s.LookaheadHours = look; s.UseImages = req.UseImages ?? true; s.UseVideos = req.UseVideos ?? true; s.AlsoInstagram = req.AlsoInstagram ?? false;
            s.UpdatedBy = u.UserId; s.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry(req.Enabled ? (was ? "autopilot.updated" : "autopilot.enabled") : "autopilot.disabled", "Product", productId.ToString(), productId, goal.Length > 120 ? goal[..120] : goal, After: new { channel, planDays, every, cap, look }), ct);
            return Results.NoContent();
        });

        g.MapPost("/products/{productId:guid}/autopilot/run", async (Guid productId, ICurrentUser u, IServiceProvider sp, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            AutopilotReport? report = null;
            var ran = await AutopilotLocks.RunAsync(productId, async () => report = await sp.GetRequiredService<AutopilotEngine>().RunAsync(productId, ct));
            return ran ? Results.Ok(new { notes = report!.Notes }) : Results.Problem(statusCode: 409, title: "The autopilot is already running for this product. Try again in a minute.");
        });
    }
}
