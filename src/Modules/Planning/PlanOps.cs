using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Planning;

public sealed record OpError(int Status, string Title, IReadOnlyList<string>? Errors = null, string? Outcome = null, string? Code = null);

public sealed class Op<T>
{
    public T? Value { get; init; }
    public OpError? Error { get; init; }
    public bool Ok => Error is null;
    public static Op<T> Of(T v) => new() { Value = v };
    public static Op<T> Fail(OpError e) => new() { Error = e };
}

public sealed record DraftSlotResult(Guid SlotId, Guid ContentItemId, bool Submitted, string? SubmitError, IReadOnlyList<string> Questions);
public sealed record ScheduleOutcome(int Scheduled, IReadOnlyList<(int Position, string? Reason)> Skipped);

/// <summary>
/// The plan operations (create, draft a post, schedule, review). The endpoints and the autopilot both call these, so a plan made by hand and a plan made by the autopilot follow exactly the same rules.
/// Permission checks belong to the caller.
/// </summary>
public sealed class PlanOps(PlanningDbContext db, IProductKnowledge knowledge, IAiGenerator ai, IContentCatalog catalog, IPostPerformanceReader perf, IConfiguration cfg,
    IContentDrafter drafter, IContentPlanner content, HookTestService tests, IClock clock, IAuditWriter audit)
{
    private static readonly Regex Placeholder = new(@"\[[^\]\r\n]{2,60}\]", RegexOptions.Compiled);
    private const int AiAttempts = 2;

    private static OpError FromAi(AiException ex)
        => new(ex.Outcome switch { AiOutcome.BudgetBlocked => 402, AiOutcome.Paused => 503, AiOutcome.Refused => 422, _ => 502 }, ex.Message, Outcome: ex.Outcome.ToString());

    private static OpError FromResult(Error e) => new(e.Kind switch { ErrorKind.Conflict => 409, ErrorKind.Policy => 422, _ => 400 }, e.Message, Code: e.Code);

    // ---------- create ----------

    public async Task<Op<Guid>> CreatePlanAsync(Guid productId, PlanSpec spec, Guid? fromReviewId, bool managed, Guid? actor, CancellationToken ct)
    {
        var errors = PlanLogic.Validate(spec, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
        if (errors.Count > 0) return Op<Guid>.Fail(new OpError(400, "The plan is not valid.", errors));

        var k = await knowledge.GetApprovedAsync(productId, ct);
        if (k is null) return Op<Guid>.Fail(new OpError(409, "This product has no approved profile yet. Approve a profile before planning."));
        if (spec.Language is not null && !k.Markets.SelectMany(m => m.Languages).Contains(spec.Language, StringComparer.OrdinalIgnoreCase))
            return Op<Guid>.Fail(new OpError(400, $"Language '{spec.Language}' is not a verified market language for this product."));

        string? learnings = null; PlanReview? from = null; GrowthPlan? fromPlan = null;
        if (fromReviewId is { } rid)
        {
            from = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct);
            fromPlan = from is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == from.PlanId && p.ProductId == productId, ct);
            if (from is null || fromPlan is null) return Op<Guid>.Fail(new OpError(400, "That review was not found for this product."));
            var r = JsonSerializer.Deserialize<ReviewResult>(from.ResultJson, PlanLogic.Json) ?? new();
            learnings = $"Headline: {r.Headline} (confidence: {r.Confidence})\nLearnings: {string.Join(" | ", r.Learnings)}\nWorked: {string.Join(" | ", r.WhatWorked)}\nDid not work: {string.Join(" | ", r.WhatDidNot)}\nSuggested next goal: {r.NextPlan.Goal}\nSuggested angles: {string.Join(" | ", r.NextPlan.SuggestedAngles.Select(a => $"{a.Angle} ({a.Why})"))}\nCaveats: {string.Join(" | ", r.Caveats)}";
        }

        var slotCount = PlanLogic.SlotCount(spec.Days, spec.EveryDays);
        var table = await LeaderboardEndpoint.BuildAsync(catalog, perf, cfg, productId, spec.Channel, ct);
        var testLines = await tests.ConcludedSummariesAsync(productId, ct);
        var system = PlanLogic.BuildPlanSystem(k, slotCount); var user = PlanLogic.BuildPlanUser(spec, slotCount, learnings, table.IsEmpty ? null : Leaderboard.ForPrompt(table));
        if (testLines.Count > 0) user += "\n\nHOOK TEST RESULTS (small samples: indications, not proof; \"no clear winner\" means the hook types performed alike). You may quote these figures:\n- " + string.Join("\n- ", testLines) + "\n";
        PlanDraft? draft = null; AiResult? last = null; decimal cost = 0; List<string> violations = [];
        try
        {
            for (var attempt = 1; attempt <= AiAttempts && draft is null; attempt++)
            {
                var prompt = attempt == 1 ? user : user + "\n\nYour previous answer broke these rules. Fix every one and return the JSON again:\n- " + string.Join("\n- ", violations);
                last = await ai.GenerateAsync(new AiRequest("plan.generate", AiTier.Strategy, system, prompt, productId, "plan.generate", 1, MaxTokens: 8000), ct);
                cost += last.EstimatedCostUsd;
                var parsed = PlanLogic.ParsePlan(last.Text, out var parseError);
                violations = parsed is null ? [parseError!] : PlanLogic.ValidatePlan(parsed, slotCount, table, k.Facts.Select(f => f.Text).Concat(k.Claims.Select(c => c.Text)).Append(k.Summary).Concat(testLines));
                if (violations.Count == 0) draft = parsed;
            }
        }
        catch (AiException ex) { return Op<Guid>.Fail(FromAi(ex)); }
        if (draft is null) return Op<Guid>.Fail(new OpError(422, "The planner could not produce a usable plan: " + string.Join(" | ", violations)));

        var now = clock.UtcNow;
        var plan = new GrowthPlan
        {
            ProductId = productId, Name = spec.Name, Goal = spec.Goal, Channel = spec.Channel, Language = spec.Language, StartDate = spec.StartDate, Days = spec.Days, EveryDays = spec.EveryDays, PostTimeUtc = spec.TimeUtc,
            Status = PlanStatus.Draft, Rationale = draft.Rationale.Trim(), MeasuresJson = JsonSerializer.Serialize(draft.Measures, PlanLogic.Json), PreviousPlanId = fromPlan?.Id, PreviousReviewId = from?.Id, Managed = managed,
            Provider = last!.Provider, Model = last.Model, CostUsd = cost, CreatedBy = actor, CreatedAt = now, UpdatedAt = now,
        };
        var times = PlanLogic.SlotTimes(spec);
        db.Plans.Add(plan);
        for (var i = 0; i < times.Count; i++)
            db.Slots.Add(new PlanSlot { PlanId = plan.Id, Position = i + 1, ScheduledFor = times[i], Angle = draft.Slots[i].Angle.Trim(), Brief = draft.Slots[i].Brief.Trim(), HookType = draft.Slots[i].HookType, Status = SlotStatus.Planned });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("plan.created", "GrowthPlan", plan.Id.ToString(), productId, plan.Name, After: new { plan.Days, slots = times.Count, plan.Channel, plan.Provider, plan.Model, plan.CostUsd, fromReview = from?.Id, managed }), ct);
        return Op<Guid>.Of(plan.Id);
    }

    // ---------- draft one post ----------

    public async Task<Op<DraftSlotResult>> DraftSlotAsync(Guid planId, Guid slotId, CancellationToken ct)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null) return Op<DraftSlotResult>.Fail(new OpError(404, "Plan not found"));
        if (plan.Status is PlanStatus.Cancelled or PlanStatus.Completed) return Op<DraftSlotResult>.Fail(new OpError(409, $"A {plan.Status.ToString().ToLowerInvariant()} plan cannot be changed."));
        var all = await db.Slots.AsNoTracking().Where(s => s.PlanId == planId).OrderBy(s => s.Position).ToListAsync(ct);
        var slot = all.FirstOrDefault(s => s.Id == slotId);
        if (slot is null) return Op<DraftSlotResult>.Fail(new OpError(404, "Slot not found"));

        // Claim the slot so a double click (or the autopilot and a person together) cannot draft it twice.
        var claimed = await db.Slots.Where(s => s.Id == slotId && s.Status == SlotStatus.Planned).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Drafting), ct);
        if (claimed == 0) return Op<DraftSlotResult>.Fail(new OpError(409, "This post is already drafted or being drafted."));
        var done = false;
        try
        {
            var others = string.Join("; ", all.Where(s => s.Id != slotId).Select(s => $"post {s.Position}: {s.Angle}"));
            var brief = $"{plan.Channel} post {slot.Position} of {all.Count} in a plan called \"{plan.Name}\". Plan goal: {plan.Goal}\nAngle for THIS post: {slot.Angle}\nGuidance: {slot.Brief}\n" +
                        $"The other posts in the series cover: {others}. Do not repeat their openings or points.\nKeep it short (under 600 characters), in the brand voice. If the facts include the product's store link, end with it exactly; never write a placeholder such as [link]." +
                        (string.IsNullOrWhiteSpace(slot.Guidance) ? "" : $"\nA reviewer rejected the previous draft of this post for these reasons. Do not repeat them: {slot.Guidance}");
            Result<DraftedContent> res;
            try { res = await drafter.DraftPostAsync(plan.ProductId, brief, plan.Channel, plan.Language, ct); }
            catch (AiException ex) { return Op<DraftSlotResult>.Fail(FromAi(ex)); }
            if (!res.IsSuccess) return Op<DraftSlotResult>.Fail(FromResult(res.Error!));
            var d = res.Value!;
            var ph = Placeholder.Match(d.Body);
            if (ph.Success) return Op<DraftSlotResult>.Fail(new OpError(422, $"The draft contains a placeholder ({ph.Value}) because the profile lacks that information. Add it to the product profile (for example the store link as a fact), approve the profile, then try again."));

            var itemId = await content.CreateAsync(new NewContent(plan.ProductId, $"{plan.Name} · Post {slot.Position}: {slot.Angle}", plan.Channel, "Post", plan.Language, d.Body, d.DraftId, null, slot.HookType), ct);
            var submitted = await content.SubmitAsync(itemId, ct);
            await db.Slots.Where(s => s.Id == slotId).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Drafted).SetProperty(s => s.ContentItemId, itemId).SetProperty(s => s.DraftedAt, clock.UtcNow).SetProperty(s => s.Attempts, s => s.Attempts + 1), ct);
            done = true;
            await audit.WriteAsync(new AuditEntry("plan.slot-drafted", "GrowthPlan", planId.ToString(), plan.ProductId, $"{plan.Name} post {slot.Position}", After: new { itemId, submitted = submitted.Ok, submitError = submitted.Error }), ct);
            return Op<DraftSlotResult>.Of(new DraftSlotResult(slotId, itemId, submitted.Ok, submitted.Error, d.Questions));
        }
        finally
        {
            if (!done) await db.Slots.Where(s => s.Id == slotId && s.Status == SlotStatus.Drafting).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Planned), CancellationToken.None);
        }
    }

    // ---------- schedule ----------

    /// <summary>
    /// Schedules every approved post of the plan at its planned time. <paramref name="autopilot"/> additionally moves a post whose time has passed to a few minutes from now,
    /// and refuses to put more than <paramref name="maxPerDay"/> posts on one UTC day for the channel.
    /// </summary>
    public async Task<Op<ScheduleOutcome>> SchedulePlanAsync(Guid planId, bool autopilot, int maxPerDay, CancellationToken ct)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null) return Op<ScheduleOutcome>.Fail(new OpError(404, "Plan not found"));
        if (plan.Status is PlanStatus.Cancelled or PlanStatus.Completed) return Op<ScheduleOutcome>.Fail(new OpError(409, $"A {plan.Status.ToString().ToLowerInvariant()} plan cannot be scheduled."));
        var slots = await db.Slots.AsNoTracking().Where(s => s.PlanId == planId && s.ContentItemId != null).OrderBy(s => s.Position).ToListAsync(ct);
        var statuses = (await content.GetStatusesAsync(slots.Select(s => s.ContentItemId!.Value).ToList(), ct)).ToDictionary(x => x.ItemId);
        var scheduled = 0; var skipped = new List<(int, string?)>();
        foreach (var s in slots)
        {
            if (!statuses.TryGetValue(s.ContentItemId!.Value, out var c)) continue;
            if (c.Status is "Scheduled" or "Publishing" or "Published") continue;
            if (c.Status != "Approved" || c.ApprovedVersion != c.CurrentVersion) { skipped.Add((s.Position, c.Status == "InReview" ? "Waiting for approval" : $"Not approved (status: {c.Status})")); continue; }
            var at = s.ScheduledFor;
            if (autopilot)
            {
                var floor = clock.UtcNow.AddMinutes(10);
                if (at < floor) at = floor;
                var day = DateOnly.FromDateTime(at.UtcDateTime);
                if (await catalog.CountOnDayAsync(plan.ProductId, plan.Channel, day, ct) >= maxPerDay) { skipped.Add((s.Position, $"Already {maxPerDay} post(s) that day")); continue; }
            }
            if (s.TestId is { } tid && await tests.SiblingOnDayAsync(tid, s.ContentItemId.Value, DateOnly.FromDateTime(at.UtcDateTime), ct)) { skipped.Add((s.Position, "Another variant of this test is already posting that day")); continue; }
            var r = await content.ScheduleAsync(s.ContentItemId.Value, at, ct);
            if (r.Ok) scheduled++; else skipped.Add((s.Position, r.Error));
        }
        if (scheduled > 0 && plan.Status == PlanStatus.Draft) { plan.Status = PlanStatus.Active; plan.UpdatedAt = clock.UtcNow; await db.SaveChangesAsync(ct); }
        await audit.WriteAsync(new AuditEntry("plan.scheduled", "GrowthPlan", planId.ToString(), plan.ProductId, plan.Name, After: new { scheduled, skipped = skipped.Count, autopilot }), ct);
        return Op<ScheduleOutcome>.Of(new ScheduleOutcome(scheduled, skipped));
    }

    // ---------- review ----------

    public async Task<Op<Guid>> ReviewPlanAsync(Guid planId, Guid? actor, CancellationToken ct)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null) return Op<Guid>.Fail(new OpError(404, "Plan not found"));
        var slots = await db.Slots.AsNoTracking().Where(s => s.PlanId == planId).OrderBy(s => s.Position).ToListAsync(ct);
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
            catch (AiException ex) { return Op<Guid>.Fail(FromAi(ex)); }
            if (result is null) return Op<Guid>.Fail(new OpError(422, "The review could not be produced without quoting figures that were not in the data: " + string.Join(" | ", violations)));
        }

        var review = new PlanReview
        {
            PlanId = planId, DataQualityJson = JsonSerializer.Serialize(q, PlanLogic.Json), FiguresJson = JsonSerializer.Serialize(figures, PlanLogic.Json), ResultJson = JsonSerializer.Serialize(result, PlanLogic.Json),
            Provider = provider, Model = model, CostUsd = cost, CreatedBy = actor, CreatedAt = clock.UtcNow,
        };
        db.Reviews.Add(review);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("plan.reviewed", "GrowthPlan", planId.ToString(), plan.ProductId, plan.Name, After: new { reviewId = review.Id, q.Published, q.Measured, result.Confidence, cost }), ct);
        return Op<Guid>.Of(review.Id);
    }
}
