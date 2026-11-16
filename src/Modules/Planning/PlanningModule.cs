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
    public static IServiceCollection AddPlanningModule(this IServiceCollection s, IConfiguration c) => s.AddModuleDbContext<PlanningDbContext>(c, PlanningDbContext.Schema);

    public sealed record CreatePlanRequest(string Name, string Goal, DateOnly StartDate, int Days, int? EveryDays, string? TimeUtc, string? Channel, string? Language, Guid? FromReviewId);

    private static readonly Regex Placeholder = new(@"\[[^\]\r\n]{2,60}\]", RegexOptions.Compiled);
    private const int AiAttempts = 2;

    private static IResult AiFailure(AiException ex)
        => Results.Problem(statusCode: ex.Outcome switch { AiOutcome.BudgetBlocked => 402, AiOutcome.Paused => 503, AiOutcome.Refused => 422, _ => 502 }, title: ex.Message,
            extensions: new Dictionary<string, object?> { ["outcome"] = ex.Outcome.ToString() });

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

        g.MapPost("/products/{productId:guid}/plans", async (Guid productId, CreatePlanRequest req, PlanningDbContext db, ICurrentUser u, IProductKnowledge knowledge, IAiGenerator ai, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var time = TimeOnly.TryParse(req.TimeUtc ?? "13:00", out var t) ? t : new TimeOnly(13, 0);
            var spec = new PlanSpec(req.Name?.Trim() ?? "", req.Goal?.Trim() ?? "", string.IsNullOrWhiteSpace(req.Channel) ? "Facebook" : req.Channel.Trim(), string.IsNullOrWhiteSpace(req.Language) ? null : req.Language.Trim(),
                req.StartDate, req.Days, req.EveryDays ?? 1, time);
            var errors = PlanLogic.Validate(spec, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [.. errors] });

            var k = await knowledge.GetApprovedAsync(productId, ct);
            if (k is null) return Results.Problem(statusCode: 409, title: "This product has no approved profile yet. Approve a profile before planning.");
            if (spec.Language is not null && !k.Markets.SelectMany(m => m.Languages).Contains(spec.Language, StringComparer.OrdinalIgnoreCase))
                return Results.Problem(statusCode: 400, title: $"Language '{spec.Language}' is not a verified market language for this product.");

            string? learnings = null; PlanReview? from = null; GrowthPlan? fromPlan = null;
            if (req.FromReviewId is { } rid)
            {
                from = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct);
                fromPlan = from is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == from.PlanId && p.ProductId == productId, ct);
                if (from is null || fromPlan is null) return Results.Problem(statusCode: 400, title: "That review was not found for this product.");
                var r = JsonSerializer.Deserialize<ReviewResult>(from.ResultJson, PlanLogic.Json) ?? new();
                learnings = $"Headline: {r.Headline} (confidence: {r.Confidence})\nLearnings: {string.Join(" | ", r.Learnings)}\nWorked: {string.Join(" | ", r.WhatWorked)}\nDid not work: {string.Join(" | ", r.WhatDidNot)}\nSuggested next goal: {r.NextPlan.Goal}\nSuggested angles: {string.Join(" | ", r.NextPlan.SuggestedAngles.Select(a => $"{a.Angle} - {a.Why}"))}\nCaveats: {string.Join(" | ", r.Caveats)}";
            }

            var slotCount = PlanLogic.SlotCount(spec.Days, spec.EveryDays);
            var system = PlanLogic.BuildPlanSystem(k, slotCount); var user = PlanLogic.BuildPlanUser(spec, slotCount, learnings);
            PlanDraft? draft = null; AiResult? last = null; decimal cost = 0; List<string> violations = [];
            try
            {
                for (var attempt = 1; attempt <= AiAttempts && draft is null; attempt++)
                {
                    var prompt = attempt == 1 ? user : user + "\n\nYour previous answer broke these rules. Fix every one and return the JSON again:\n- " + string.Join("\n- ", violations);
                    last = await ai.GenerateAsync(new AiRequest("plan.generate", AiTier.Strategy, system, prompt, productId, "plan.generate", 1, MaxTokens: 8000), ct);
                    cost += last.EstimatedCostUsd;
                    var parsed = PlanLogic.ParsePlan(last.Text, out var parseError);
                    violations = parsed is null ? [parseError!] : PlanLogic.ValidatePlan(parsed, slotCount);
                    if (violations.Count == 0) draft = parsed;
                }
            }
            catch (AiException ex) { return AiFailure(ex); }
            if (draft is null) return Results.Problem(statusCode: 422, title: "The planner could not produce a usable plan: " + string.Join(" | ", violations));

            var now = clock.UtcNow;
            var plan = new GrowthPlan
            {
                ProductId = productId, Name = spec.Name, Goal = spec.Goal, Channel = spec.Channel, Language = spec.Language, StartDate = spec.StartDate, Days = spec.Days, EveryDays = spec.EveryDays, PostTimeUtc = spec.TimeUtc,
                Status = PlanStatus.Draft, Rationale = draft.Rationale.Trim(), MeasuresJson = JsonSerializer.Serialize(draft.Measures, PlanLogic.Json), PreviousPlanId = fromPlan?.Id, PreviousReviewId = from?.Id,
                Provider = last!.Provider, Model = last.Model, CostUsd = cost, CreatedBy = u.UserId, CreatedAt = now, UpdatedAt = now,
            };
            var times = PlanLogic.SlotTimes(spec);
            db.Plans.Add(plan);
            for (var i = 0; i < times.Count; i++)
                db.Slots.Add(new PlanSlot { PlanId = plan.Id, Position = i + 1, ScheduledFor = times[i], Angle = draft.Slots[i].Angle.Trim(), Brief = draft.Slots[i].Brief.Trim(), Status = SlotStatus.Planned });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("plan.created", "GrowthPlan", plan.Id.ToString(), productId, plan.Name, After: new { plan.Days, slots = times.Count, plan.Channel, plan.Provider, plan.Model, plan.CostUsd, fromReview = from?.Id }), ct);
            return Results.Created($"/api/v1/plans/{plan.Id}", new { plan.Id });
        });

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
                        s.Id, s.Position, s.ScheduledFor, s.Angle, s.Brief, s.Status, s.ContentItemId,
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
        g.MapPost("/plans/{id:guid}/slots/{slotId:guid}/draft", async (Guid id, Guid slotId, PlanningDbContext db, ICurrentUser u, IContentDrafter drafter, IContentPlanner content, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.ContentWrite) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            if (plan.Status is PlanStatus.Cancelled or PlanStatus.Completed) return Results.Problem(statusCode: 409, title: $"A {plan.Status.ToString().ToLowerInvariant()} plan cannot be changed.");
            var all = await db.Slots.AsNoTracking().Where(s => s.PlanId == id).OrderBy(s => s.Position).ToListAsync(ct);
            var slot = all.FirstOrDefault(s => s.Id == slotId);
            if (slot is null) return Results.NotFound();

            // Claim the slot so a double click cannot draft it twice.
            var claimed = await db.Slots.Where(s => s.Id == slotId && s.Status == SlotStatus.Planned).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Drafting), ct);
            if (claimed == 0) return Results.Problem(statusCode: 409, title: "This post is already drafted or being drafted.");
            var done = false;
            try
            {
                var others = string.Join("; ", all.Where(s => s.Id != slotId).Select(s => $"post {s.Position}: {s.Angle}"));
                var brief = $"{plan.Channel} post {slot.Position} of {all.Count} in a plan called \"{plan.Name}\". Plan goal: {plan.Goal}\nAngle for THIS post: {slot.Angle}\nGuidance: {slot.Brief}\n" +
                            $"The other posts in the series cover: {others}. Do not repeat their openings or points.\nKeep it short (under 600 characters), in the brand voice. If the facts include the product's store link, end with it exactly; never write a placeholder such as [link].";
                Result<DraftedContent> res;
                try { res = await drafter.DraftPostAsync(plan.ProductId, brief, plan.Channel, plan.Language, ct); }
                catch (AiException ex) { return AiFailure(ex); }
                if (!res.IsSuccess) return Fail(res.Error!);
                var d = res.Value!;
                var ph = Placeholder.Match(d.Body);
                if (ph.Success) return Results.Problem(statusCode: 422, title: $"The draft contains a placeholder ({ph.Value}) because the profile lacks that information. Add it to the product profile (for example the store link as a fact), approve the profile, then try again.");

                var itemId = await content.CreateAsync(new NewContent(plan.ProductId, $"{plan.Name} · Post {slot.Position}: {slot.Angle}", plan.Channel, "Post", plan.Language, d.Body, d.DraftId), ct);
                var submitted = await content.SubmitAsync(itemId, ct);
                await db.Slots.Where(s => s.Id == slotId).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Drafted).SetProperty(s => s.ContentItemId, itemId).SetProperty(s => s.DraftedAt, clock.UtcNow), ct);
                done = true;
                await audit.WriteAsync(new AuditEntry("plan.slot-drafted", "GrowthPlan", id.ToString(), plan.ProductId, $"{plan.Name} post {slot.Position}", After: new { itemId, submitted = submitted.Ok, submitError = submitted.Error }), ct);
                return Results.Ok(new { slotId, contentItemId = itemId, submitted = submitted.Ok, submitError = submitted.Error, questions = d.Questions });
            }
            finally
            {
                if (!done) await db.Slots.Where(s => s.Id == slotId && s.Status == SlotStatus.Drafting).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Planned), CancellationToken.None);
            }
        });

        // Schedules every post of the plan that has been approved, at its planned time. Posts still awaiting approval are left alone and reported.
        g.MapPost("/plans/{id:guid}/schedule", async (Guid id, PlanningDbContext db, ICurrentUser u, IContentPlanner content, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            if (plan.Status is PlanStatus.Cancelled or PlanStatus.Completed) return Results.Problem(statusCode: 409, title: $"A {plan.Status.ToString().ToLowerInvariant()} plan cannot be scheduled.");
            var slots = await db.Slots.AsNoTracking().Where(s => s.PlanId == id && s.ContentItemId != null).OrderBy(s => s.Position).ToListAsync(ct);
            var statuses = (await content.GetStatusesAsync(slots.Select(s => s.ContentItemId!.Value).ToList(), ct)).ToDictionary(x => x.ItemId);
            var scheduled = 0; var skipped = new List<object>();
            foreach (var s in slots)
            {
                if (!statuses.TryGetValue(s.ContentItemId!.Value, out var c)) continue;
                if (c.Status is "Scheduled" or "Publishing" or "Published") continue;
                if (c.Status != "Approved" || c.ApprovedVersion != c.CurrentVersion) { skipped.Add(new { s.Position, reason = c.Status == "InReview" ? "Waiting for approval" : $"Not approved (status: {c.Status})" }); continue; }
                var r = await content.ScheduleAsync(s.ContentItemId.Value, s.ScheduledFor, ct);
                if (r.Ok) scheduled++; else skipped.Add(new { s.Position, reason = r.Error });
            }
            if (scheduled > 0 && plan.Status == PlanStatus.Draft) { plan.Status = PlanStatus.Active; plan.UpdatedAt = clock.UtcNow; await db.SaveChangesAsync(ct); }
            await audit.WriteAsync(new AuditEntry("plan.scheduled", "GrowthPlan", id.ToString(), plan.ProductId, plan.Name, After: new { scheduled, skipped = skipped.Count }), ct);
            return Results.Ok(new { scheduled, skipped });
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
        g.MapPost("/plans/{id:guid}/review", async (Guid id, PlanningDbContext db, ICurrentUser u, IContentPlanner content, IPostPerformanceReader perf, IAiGenerator ai, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null || !u.HasPermission(Permissions.CampaignsManage) || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(plan.ProductId, ct)) return Results.NotFound();
            var slots = await db.Slots.AsNoTracking().Where(s => s.PlanId == id).OrderBy(s => s.Position).ToListAsync(ct);
            var perfs = (await perf.GetAsync(slots.Where(s => s.ContentItemId != null).Select(s => s.ContentItemId!.Value).ToList(), ct)).ToDictionary(p => p.ContentItemId);
            var figures = slots.Select(s =>
            {
                PostPerformance? p = s.ContentItemId is { } cid && perfs.TryGetValue(cid, out var x) ? x : null;
                return new SlotFigures(s.Position, s.Angle, p?.PublishedAt, p?.Url, p?.Reactions, p?.Comments, p?.Shares, p?.Views, p?.Reach, p?.Clicks, p?.Note);
            }).ToList();
            var q = PlanLogic.Quality(figures);
            var measures = JsonSerializer.Deserialize<List<Measure>>(plan.MeasuresJson, PlanLogic.Json) ?? [];

            ReviewResult? result; string provider = "none", model = ""; decimal cost = 0;
            if (q.Measured == 0) result = PlanLogic.NotEnoughData(q);
            else
            {
                result = null; var system = PlanLogic.BuildReviewSystem(); var user = PlanLogic.BuildReviewUser(plan, measures, figures, q); List<string> violations = [];
                try
                {
                    for (var attempt = 1; attempt <= AiAttempts && result is null; attempt++)
                    {
                        var prompt = attempt == 1 ? user : user + "\n\nYour previous answer broke these rules. Fix every one and return the JSON again:\n- " + string.Join("\n- ", violations);
                        var r = await ai.GenerateAsync(new AiRequest("plan.review", AiTier.Strategy, system, prompt, plan.ProductId, "plan.review", 1, MaxTokens: 6000), ct);
                        cost += r.EstimatedCostUsd; provider = r.Provider; model = r.Model;
                        var parsed = PlanLogic.ParseReview(r.Text, out var err);
                        violations = parsed is null ? [err!] : PlanLogic.ValidateReview(parsed, figures, q);
                        if (violations.Count == 0) result = parsed;
                    }
                }
                catch (AiException ex) { return AiFailure(ex); }
                if (result is null) return Results.Problem(statusCode: 422, title: "The review could not be produced without quoting figures that were not in the data: " + string.Join(" | ", violations));
            }

            var review = new PlanReview
            {
                PlanId = id, DataQualityJson = JsonSerializer.Serialize(q, PlanLogic.Json), FiguresJson = JsonSerializer.Serialize(figures, PlanLogic.Json), ResultJson = JsonSerializer.Serialize(result, PlanLogic.Json),
                Provider = provider, Model = model, CostUsd = cost, CreatedBy = u.UserId, CreatedAt = clock.UtcNow,
            };
            db.Reviews.Add(review);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("plan.reviewed", "GrowthPlan", id.ToString(), plan.ProductId, plan.Name, After: new { reviewId = review.Id, q.Published, q.Measured, result.Confidence, cost }), ct);
            return Results.Created($"/api/v1/plans/{id}", new { review.Id });
        });

        return app;
    }
}
