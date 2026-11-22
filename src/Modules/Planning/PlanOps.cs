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

public sealed record DraftSlotResult(Guid SlotId, Guid ContentItemId, bool Submitted, string? SubmitError, IReadOnlyList<string> Questions, string? MediaNote = null);

/// <summary>Which kinds of media the caller allows for a post. Null means whatever the slot asks for.</summary>
public sealed record MediaPolicy(bool Images, bool Videos);
public sealed record ScheduleOutcome(int Scheduled, IReadOnlyList<(int Position, string? Reason)> Skipped);

/// <summary>
/// The plan operations (create, draft a post, schedule, review). The endpoints and the autopilot both call these, so a plan made by hand and a plan made by the autopilot follow exactly the same rules.
/// Permission checks belong to the caller.
/// </summary>
public sealed class PlanOps(PlanningDbContext db, IProductKnowledge knowledge, IAiGenerator ai, IContentCatalog catalog, IPostPerformanceReader perf, IConfiguration cfg,
    IContentDrafter drafter, IContentPlanner content, HookTestService tests, LearningService register, IAssetCatalog assets, IImageMaker maker, IClock clock, IAuditWriter audit, ITrackedLinks links)
{
    private static readonly Regex Placeholder = new(@"\[[^\]\r\n]{2,60}\]", RegexOptions.Compiled);
    private const int AiAttempts = 2;

    private static OpError FromAi(AiException ex)
        => new(ex.Outcome switch { AiOutcome.BudgetBlocked => 402, AiOutcome.Paused => 503, AiOutcome.Refused => 422, _ => 502 }, ex.Message, Outcome: ex.Outcome.ToString());

    private static OpError FromResult(Error e) => new(e.Kind switch { ErrorKind.Conflict => 409, ErrorKind.Policy => 422, _ => 400 }, e.Message, Code: e.Code);

    // ---------- create ----------

    public async Task<Op<Guid>> CreatePlanAsync(Guid productId, PlanSpec spec, Guid? fromReviewId, bool managed, Guid? actor, CancellationToken ct, VisualOptions? visuals = null)
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

        var slotCount = PlanLogic.SlotCount(spec.Days, spec.EveryDays, PlanLogic.PerDay(spec));
        var table = await LeaderboardEndpoint.BuildAsync(catalog, perf, cfg, productId, spec.Channel, ct);
        var testLines = await tests.ConcludedSummariesAsync(productId, ct);
        var open = await register.OpenAsync(productId, ct);
        var system = PlanLogic.BuildPlanSystem(k, slotCount, visuals, open); var user = PlanLogic.BuildPlanUser(spec, slotCount, learnings, table.IsEmpty ? null : Leaderboard.ForPrompt(table));
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
                violations = parsed is null ? [parseError!] : PlanLogic.ValidatePlan(parsed, slotCount, table, k.Facts.Select(f => f.Text).Concat(k.Claims.Select(c => c.Text)).Append(k.Summary).Concat(testLines).Concat(open.SelectMany(h => new[] { h.Statement, h.HowToTest, h.Evidence })), visuals, maker.CheckBrief, open.Select(h => h.Id).ToList());
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
            db.Slots.Add(new PlanSlot { PlanId = plan.Id, Position = i + 1, ScheduledFor = times[i], Angle = draft.Slots[i].Angle.Trim(), Brief = draft.Slots[i].Brief.Trim(), HookType = draft.Slots[i].HookType, Status = SlotStatus.Planned, Visual = visuals is null ? null : draft.Slots[i].Visual, ImageBrief = visuals is not null && draft.Slots[i].Visual == "image" ? draft.Slots[i].ImageBrief?.Trim() : null, HypothesisId = open.Any(h => h.Id.ToString() == draft.Slots[i].Tests) ? Guid.Parse(draft.Slots[i].Tests!) : null });
        await db.SaveChangesAsync(ct);
        await register.MarkTestedAsync(plan.Id, [.. draft.Slots.Select(s => Guid.TryParse(s.Tests, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty && open.Any(h => h.Id == g)).Distinct()], ct);
        await audit.WriteAsync(new AuditEntry("plan.created", "GrowthPlan", plan.Id.ToString(), productId, plan.Name, After: new { plan.Days, slots = times.Count, plan.Channel, plan.Provider, plan.Model, plan.CostUsd, fromReview = from?.Id, managed }), ct);
        return Op<Guid>.Of(plan.Id);
    }

    // ---------- draft one post ----------

    public async Task<Op<DraftSlotResult>> DraftSlotAsync(Guid planId, Guid slotId, CancellationToken ct, MediaPolicy? policy = null)
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
            var media = await ChooseMediaAsync(plan, slot, policy ?? new MediaPolicy(true, true), ct);
            if (media.Describe is not null) brief += $"\nA {media.Describe} will be published with this post. Write only the text that goes with it. Do not describe what it shows beyond that, and never call an illustration a screenshot or real gameplay.";
            Result<DraftedContent> res;
            try { res = await drafter.DraftPostAsync(plan.ProductId, brief, plan.Channel, plan.Language, ct); }
            catch (AiException ex) { return Op<DraftSlotResult>.Fail(FromAi(ex)); }
            if (!res.IsSuccess) return Op<DraftSlotResult>.Fail(FromResult(res.Error!));
            var d = res.Value!;
            var ph = Placeholder.Match(d.Body);
            if (ph.Success) return Op<DraftSlotResult>.Fail(new OpError(422, $"The draft contains a placeholder ({ph.Value}) because the profile lacks that information. Add it to the product profile (for example the store link as a fact), approve the profile, then try again."));

            // the product's own links in the text become tracked short links before the text is approved, so the approved text is exactly what goes out
            var (text, linkIds) = await TrackLinksAsync(plan, slot.Position, d.Body, ct);
            var itemId = await content.CreateAsync(new NewContent(plan.ProductId, $"{plan.Name} · Post {slot.Position}: {slot.Angle}", plan.Channel, media.Format, plan.Language, text, d.DraftId, media.Assets, slot.HookType), ct);
            await links.AttachAsync(linkIds, itemId, ct);
            var submitted = await content.SubmitAsync(itemId, ct);
            await db.Slots.Where(s => s.Id == slotId).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Drafted).SetProperty(s => s.ContentItemId, itemId).SetProperty(s => s.DraftedAt, clock.UtcNow).SetProperty(s => s.Attempts, s => s.Attempts + 1), ct);
            done = true;
            await audit.WriteAsync(new AuditEntry("plan.slot-drafted", "GrowthPlan", planId.ToString(), plan.ProductId, $"{plan.Name} post {slot.Position}", After: new { itemId, submitted = submitted.Ok, submitError = submitted.Error }), ct);
            return Op<DraftSlotResult>.Of(new DraftSlotResult(slotId, itemId, submitted.Ok, submitted.Error, d.Questions, media.Note));
        }
        finally
        {
            if (!done) await db.Slots.Where(s => s.Id == slotId && s.Status == SlotStatus.Drafting).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SlotStatus.Planned), CancellationToken.None);
        }
    }

    /// <summary>
    /// Replaces each address in the text that comes from the verified profile (the store page) with a tracked short link. Anything else is left alone.
    /// If links cannot be made (no public address configured, or an error), the text keeps its original addresses: tracking never blocks a post.
    /// </summary>
    private async Task<(string Text, List<Guid> LinkIds)> TrackLinksAsync(GrowthPlan plan, int position, string body, CancellationToken ct)
    {
        var ids = new List<Guid>();
        try
        {
            var k = await knowledge.GetApprovedAsync(plan.ProductId, ct);
            if (k is null) return (body, ids);
            var own = AutopilotGates.ProfileUrls(k);
            foreach (var url in AutopilotGates.UrlsIn(body).Where(own.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var made = await links.CreateAsync(plan.ProductId, url, plan.Channel, k.ProductName, $"{plan.Name} · post {position}", ct);
                if (made is null) continue;
                body = body.Replace(url, made.ShortUrl, StringComparison.OrdinalIgnoreCase); ids.Add(made.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* keep the original addresses */ }
        return (body, ids);
    }

    // ---------- media ----------

    private sealed record MediaChoice(IReadOnlyList<Guid> Assets, string Format, string? Note, string? Describe);

    private static bool RightsClear(AssetInfo a) => a.Rights is "Unrestricted" or "LicensedWithLimits";

    /// <summary>Files the autopilot may use: not yet on any live post, not AI-made, with clear usage rights, oldest first.</summary>
    private async Task<List<AssetInfo>> FreeFilesAsync(Guid productId, CancellationToken ct)
    {
        var used = await catalog.GetUsedAssetIdsAsync(productId, ct);
        return [.. (await assets.ListAsync(productId, ct)).Where(a => !used.Contains(a.Id) && !a.IsSynthetic && RightsClear(a))];
    }

    private bool FitsReel(AssetInfo a, Guid productId) => MediaRules.IsVideo(a) && MediaRules.Validate("Reel", [a], productId, clock.UtcNow).Count == 0;
    private bool FitsPost(AssetInfo a, Guid productId) => a.ContentType is "image/jpeg" or "image/png" &&   // the formats Facebook documents for photos
         MediaRules.Validate("Post", [a], productId, clock.UtcNow).Count == 0;

    /// <summary>How many uploaded vertical videos are free to become Reels.</summary>
    public async Task<int> CountFreeVideosAsync(Guid productId, CancellationToken ct) => (await FreeFilesAsync(productId, ct)).Count(a => FitsReel(a, productId));

    /// <summary>
    /// Picks the media for a post the planner marked as image or video. The product's own uploads come first (a Reel from a free vertical video; a picture from a free uploaded image),
    /// then an AI cartoon picture from the slot's brief. If none works the post goes out as text, and the note says why. AI pictures obey the same caps, budgets and brief rules as the editor.
    /// </summary>
    private async Task<MediaChoice> ChooseMediaAsync(GrowthPlan plan, PlanSlot slot, MediaPolicy policy, CancellationToken ct)
    {
        var visual = slot.Visual ?? "text";
        var text = new MediaChoice([], "Post", null, null);
        if (visual == "text") return text;
        var free = await FreeFilesAsync(plan.ProductId, ct);
        string? why = null;
        if (visual == "video")
        {
            if (!policy.Videos) why = "Videos are switched off.";
            else if (free.FirstOrDefault(a => FitsReel(a, plan.ProductId)) is { } v) return new([v.Id], "Reel", $"Reel from your video \"{v.Name}\".", $"short vertical video named \"{v.Name}\"");
            else why = "No free vertical video was available.";
        }
        if (!policy.Images) return text with { Note = (why is null ? "" : why + " ") + "Pictures are switched off, so this is text only." };
        if (free.FirstOrDefault(a => FitsPost(a, plan.ProductId)) is { } img) return new([img.Id], "Post", $"Used your picture \"{img.Name}\".", $"picture named \"{img.Name}\"");
        if (string.IsNullOrWhiteSpace(slot.ImageBrief)) return text with { Note = (why is null ? "" : why + " ") + "No picture was available, so this is text only." };
        try
        {
            var made = await maker.MakeAsync(plan.ProductId, slot.ImageBrief!, "1:1", ct);
            if (made.AssetId is { } id) return new([id], "Post", (why is null ? "" : why + " ") + "Made an AI cartoon picture.", $"flat cartoon-style illustration ({slot.ImageBrief}); it is an illustration, not a screenshot or real gameplay");
            return text with { Note = $"{why} No picture: {made.Error}".Trim() };
        }
        catch (AiException ex) { return text with { Note = $"{why} No picture ({ex.Message}), so this is text only.".Trim() }; }
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
        var openH = await register.OpenAsync(plan.ProductId, ct);
        var openIds = openH.Select(h => h.Id).ToList();

        ReviewResult? result; string provider = "none", model = ""; decimal cost = 0;
        if (q.Measured == 0) result = PlanLogic.NotEnoughData(q);
        else
        {
            result = null; var system = PlanLogic.BuildReviewSystem(withHypotheses: true); var user = PlanLogic.BuildReviewUser(plan, measures, figures, q, openH); List<string> violations = [];
            try
            {
                for (var attempt = 1; attempt <= AiAttempts && result is null; attempt++)
                {
                    var prompt = attempt == 1 ? user : user + "\n\nYour previous answer broke these rules. Fix every one and return the JSON again:\n- " + string.Join("\n- ", violations);
                    var r = await ai.GenerateAsync(new AiRequest("plan.review", AiTier.Strategy, system, prompt, plan.ProductId, "plan.review", 1, MaxTokens: 6000), ct);
                    cost += r.EstimatedCostUsd; provider = r.Provider; model = r.Model;
                    var parsed = PlanLogic.ParseReview(r.Text, out var err);
                    violations = parsed is null ? [err!] : PlanLogic.ValidateReview(parsed, figures, q, openIds);
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
        await register.ApplyReviewAsync(plan.ProductId, review.Id, result, ct);
        await audit.WriteAsync(new AuditEntry("plan.reviewed", "GrowthPlan", planId.ToString(), plan.ProductId, plan.Name, After: new { reviewId = review.Id, q.Published, q.Measured, result.Confidence, cost }), ct);
        return Op<Guid>.Of(review.Id);
    }
}
