using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Planning;

public sealed record PlanSpec(string Name, string Goal, string Channel, string? Language, DateOnly StartDate, int Days, int EveryDays, TimeOnly TimeUtc);

public sealed class Measure { public string Name { get; set; } = ""; public string Why { get; set; } = ""; }
public sealed class PlannedPost { public string Angle { get; set; } = ""; public string Brief { get; set; } = ""; public string? HookType { get; set; } public string? Visual { get; set; } public string? ImageBrief { get; set; } }

/// <summary>What the planner may use for pictures and video in this plan.</summary>
public sealed record VisualOptions(bool Images, int VideosAvailable);
/// <summary>What the planner (AI) proposes. Dates and counts are never the AI's job: code works them out from the cadence.</summary>
public sealed class PlanDraft
{
    public string Rationale { get; set; } = "";
    public List<Measure> Measures { get; set; } = [];
    public List<PlannedPost> Slots { get; set; } = [];
}

/// <summary>One planned post with whatever the network has reported for it. A null figure means "not reported", never zero.</summary>
public sealed record SlotFigures(int Position, string Angle, DateTimeOffset? PublishedAt, string? Url, int? Reactions, int? Comments, int? Shares, long? Views, long? Reach, int? Clicks, string? Note)
{
    /// <summary>Reactions + comments + shares, only when all three are known.</summary>
    public int? Engagements => Reactions is null || Comments is null || Shares is null ? null : Reactions + Comments + Shares;
}

public sealed record DataQuality(int Slots, int Published, int Measured, int WithViews);

public sealed class NextAngle { public string Angle { get; set; } = ""; public string Why { get; set; } = ""; }
public sealed class NextPlan { public string Goal { get; set; } = ""; public List<NextAngle> SuggestedAngles { get; set; } = []; }
public sealed class ReviewResult
{
    public string Headline { get; set; } = "";
    /// <summary>low | medium | high: how far the evidence supports the conclusions.</summary>
    public string Confidence { get; set; } = "low";
    public List<string> WhatWorked { get; set; } = [];
    public List<string> WhatDidNot { get; set; } = [];
    public List<string> Learnings { get; set; } = [];
    public NextPlan NextPlan { get; set; } = new();
    public List<string> Caveats { get; set; } = [];
}

public static class PlanLogic
{
    public const int MaxSlots = 31;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static int SlotCount(int days, int everyDays) => everyDays <= 0 ? 0 : (days + everyDays - 1) / everyDays;

    public static List<string> Validate(PlanSpec s, DateOnly today)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 200) e.Add("A name of up to 200 characters is required.");
        if (string.IsNullOrWhiteSpace(s.Goal) || s.Goal.Length > 2000) e.Add("A goal of up to 2000 characters is required.");
        if (string.IsNullOrWhiteSpace(s.Channel) || s.Channel.Length > 60) e.Add("A channel is required.");
        if (s.Days is < 1 or > 60) e.Add("A plan covers between 1 and 60 days.");
        if (s.EveryDays is < 1 or > 14) e.Add("Posts can be 1 to 14 days apart.");
        if (s.Days is >= 1 and <= 60 && s.EveryDays is >= 1 and <= 14 && SlotCount(s.Days, s.EveryDays) > MaxSlots) e.Add($"That is more than {MaxSlots} posts. Shorten the plan or space the posts out.");
        if (s.StartDate < today) e.Add("The plan cannot start in the past.");
        return e;
    }

    public static List<DateTimeOffset> SlotTimes(PlanSpec s)
    {
        var list = new List<DateTimeOffset>();
        for (var d = 0; d < s.Days; d += s.EveryDays)
            list.Add(new DateTimeOffset(s.StartDate.AddDays(d).ToDateTime(s.TimeUtc), TimeSpan.Zero));
        return list;
    }

    // ---------- planning ----------

    public static string BuildPlanSystem(ApprovedKnowledge k, int slots, VisualOptions? visuals = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are the growth planner for the product \"{k.ProductName}\" ({k.Kind}, {k.Lifecycle}).");
        sb.AppendLine("You plan a short series of social posts. You do not write the posts: you decide what each post should be about and why.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine($"1. Return exactly {slots} slots, in posting order. Every slot has a different angle (a short label) and a brief (1-3 sentences telling the writer what to cover).");
        sb.AppendLine("2. Base every angle ONLY on the VERIFIED FACTS and APPROVED CLAIMS below. Never plan around a feature, price, statistic, award or testimonial that is not listed.");
        sb.AppendLine("3. Vary the kind of post across the series (introduction, feature, audience scenario, a question to followers, a call to download or act) so the results show what the audience responds to.");
        sb.AppendLine("4. \"rationale\": 2-5 sentences on why this mix suits the goal. \"measures\": 2-5 things to look at afterwards to judge whether the plan worked, each with a short reason. Name only things a social network reports for a post (reactions, comments, shares, views, reach, link clicks).");
        sb.AppendLine("5. Do not promise results. Do not state any number you were not given.");
        sb.AppendLine("6. Give every slot a \"hookType\", exactly one of: " + string.Join(", ", HookTypes.All) + ". Use a mix across the series.");
        if (visuals is not null)
        {
            sb.AppendLine("7. Give every slot a \"visual\": \"text\" (words only), \"image\" or \"video\". Choose whatever is most likely to reach the goal. Where the LEADERBOARD has solid evidence for a visual type, favour it; otherwise mix them so the results teach us something (for example about a third text-only, a third with a picture, the rest video where possible).");
            sb.AppendLine(visuals.VideosAvailable > 0
                ? $"   \"video\" is a Reel made from the owner's own uploaded vertical videos. Only {visuals.VideosAvailable} such video(s) are available for this plan, so use \"video\" at most {visuals.VideosAvailable} time(s)."
                : "   No uploaded videos are available, so never use \"video\".");
            sb.AppendLine(visuals.Images
                ? "   For \"image\" also give \"imageBrief\": ONE sentence describing a flat cartoon-style scene that illustrates the post. It must contain NO people, faces, crowds or hands, NO phone or game screens, gameplay or app interface, NO logos, text, words or numbers. Only objects and scenes (for example a dice rolling across a colourful board, a trophy on a podium made of game pieces)."
                : "   Pictures are switched off, so never use \"image\".");
        }
        sb.AppendLine();
        sb.AppendLine("PRODUCT SUMMARY: " + k.Summary);
        sb.AppendLine("PLATFORMS: " + (k.Platforms.Count == 0 ? "(not specified)" : string.Join(", ", k.Platforms)));
        sb.AppendLine("MARKETS: " + (k.Markets.Count == 0 ? "(none verified)" : string.Join("; ", k.Markets.Select(m => $"{m.Country} [{string.Join('/', m.Languages)}]"))));
        sb.AppendLine("VERIFIED FACTS:");
        foreach (var f in k.Facts) sb.AppendLine($"- [{f.Category}] {f.Text}");
        sb.AppendLine("APPROVED CLAIMS:");
        foreach (var c in k.Claims.Where(c => !c.Prohibited)) sb.AppendLine($"- {c.Text}");
        sb.AppendLine("PROHIBITED CLAIMS (never plan around these):");
        foreach (var c in k.Claims.Where(c => c.Prohibited)) sb.AppendLine($"- {c.Text}");
        sb.AppendLine($"BRAND VOICE: tone={k.Tone ?? "(unspecified)"}; do: {string.Join("; ", k.Dos)}; don't: {string.Join("; ", k.Donts)}");
        sb.AppendLine();
        sb.AppendLine("Respond with ONE JSON object and nothing else: {\"rationale\": string, \"measures\": [{\"name\": string, \"why\": string}], \"slots\": [{\"angle\": string, \"brief\": string, \"hookType\": string, \"visual\": string (only when visuals are allowed), \"imageBrief\": string (only for image)}]}");
        return sb.ToString();
    }

    public static string BuildPlanUser(PlanSpec s, int slots, string? learnings, string? leaderboard = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Plan name: {s.Name}");
        sb.AppendLine($"Goal: {s.Goal}");
        sb.AppendLine($"Channel: {s.Channel}{(string.IsNullOrWhiteSpace(s.Language) ? "" : $" (language: {s.Language})")}");
        sb.AppendLine($"Length: {s.Days} day(s), {slots} post(s), one every {s.EveryDays} day(s), starting {s.StartDate:yyyy-MM-dd}.");
        if (!string.IsNullOrWhiteSpace(leaderboard))
        {
            sb.AppendLine();
            sb.AppendLine(leaderboard);
            sb.AppendLine("Use the leaderboard: favour what has solid evidence, and test a format or hook type that is missing or marked TOO FEW. Quote only figures that appear in it; never compute a percentage or a figure of your own.");
        }
        if (!string.IsNullOrWhiteSpace(learnings))
        {
            sb.AppendLine();
            sb.AppendLine("WHAT THE PREVIOUS PLAN TAUGHT US (use it: do more of what worked, and test new angles where it did not; do not treat thin evidence as proof):");
            sb.AppendLine(learnings);
        }
        return sb.ToString();
    }

    public static PlanDraft? ParsePlan(string text, out string? error)
    {
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) { error = "The answer was not a JSON object."; return null; }
        try { error = null; return JsonSerializer.Deserialize<PlanDraft>(text[start..(end + 1)], Json) ?? throw new JsonException("null"); }
        catch (JsonException ex) { error = "The answer was not valid JSON: " + ex.Message; return null; }
    }

    public static List<string> ValidatePlan(PlanDraft d, int expectedSlots, LeaderboardResult? table = null, IEnumerable<string?>? profileTexts = null, VisualOptions? visuals = null, Func<string, string?>? checkBrief = null)
    {
        var v = new List<string>();
        if (visuals is not null)
        {
            if (d.Slots.Any(s => s.Visual is not ("text" or "image" or "video"))) v.Add("Every slot needs a visual: text, image or video.");
            if (!visuals.Images && d.Slots.Any(s => s.Visual == "image")) v.Add("Pictures are switched off: do not use image.");
            var videos = d.Slots.Count(s => s.Visual == "video");
            if (videos > visuals.VideosAvailable) v.Add($"You used video {videos} time(s) but only {visuals.VideosAvailable} uploaded video(s) are available.");
            foreach (var s in d.Slots.Where(s => s.Visual == "image"))
            {
                var why = string.IsNullOrWhiteSpace(s.ImageBrief) || s.ImageBrief.Length > 300 ? "An image needs an imageBrief of up to 300 characters." : checkBrief?.Invoke(s.ImageBrief);
                if (why is not null) { v.Add($"imageBrief \"{(s.ImageBrief ?? "").Trim()}\" is not allowed: {why}"); break; }
            }
        }
        if (d.Slots.Any(s => !HookTypes.IsValid(s.HookType))) v.Add("hookType must be exactly one of: " + string.Join(", ", HookTypes.All) + ".");
        var bad = Leaderboard.FiguresNotInTable(new[] { d.Rationale }.Concat(d.Measures.SelectMany(m => new[] { m.Name, m.Why })).Concat(d.Slots.SelectMany(s => new[] { s.Angle, s.Brief })), table, profileTexts);
        if (bad.Count > 0) v.Add("These figures are not in the leaderboard you were given: " + string.Join(", ", bad) + ". Quote only figures from the leaderboard; if there is none, quote no figures.");
        if (d.Slots.Count != expectedSlots) v.Add($"Return exactly {expectedSlots} slots (you returned {d.Slots.Count}).");
        if (d.Slots.Any(s => string.IsNullOrWhiteSpace(s.Angle) || string.IsNullOrWhiteSpace(s.Brief))) v.Add("Every slot needs an angle and a brief.");
        if (d.Slots.Any(s => s.Angle.Length > 300 || s.Brief.Length > 2000)) v.Add("An angle must be under 300 characters and a brief under 2000.");
        if (d.Slots.Select(s => s.Angle.Trim().ToLowerInvariant()).Distinct().Count() != d.Slots.Count) v.Add("Every slot must have a different angle.");
        if (string.IsNullOrWhiteSpace(d.Rationale)) v.Add("A rationale is required.");
        if (d.Measures.Count is < 1 or > 8 || d.Measures.Any(m => string.IsNullOrWhiteSpace(m.Name))) v.Add("Give between 1 and 8 named measures.");
        return v;
    }

    // ---------- review ----------

    public static DataQuality Quality(IReadOnlyList<SlotFigures> f)
        => new(f.Count, f.Count(x => x.PublishedAt is not null), f.Count(x => x.Engagements is not null), f.Count(x => x.Views is not null));

    private static string Cell(object? v) => v is null ? "not reported" : Convert.ToString(v, CultureInfo.InvariantCulture)!;

    public static string BuildReviewSystem()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You review how a short social-media plan performed and advise on the next one. You are given the plan's goal, each post's angle, and the figures the social network reported.");
        sb.AppendLine();
        sb.AppendLine("RULES (non-negotiable):");
        sb.AppendLine("1. Use ONLY the figures given. \"not reported\" means the network did not supply it: it is NOT zero and you must not estimate it.");
        sb.AppendLine("2. Do not invent numbers, percentages or benchmarks. Quote figures exactly as given, and only for the posts they belong to. Avoid computing percentages.");
        sb.AppendLine("3. A handful of posts is weak evidence. Say what the figures SHOW (for example \"post 3 had the most reactions\") but do not claim causes you cannot know (timing, audience mood, algorithm). Frame explanations as hypotheses to test.");
        sb.AppendLine("4. Set \"confidence\" honestly: \"low\" when fewer than 5 posts have engagement figures or views/reach are mostly missing; \"medium\" or \"high\" only with clearly sufficient, consistent data.");
        sb.AppendLine("5. If the data cannot support a conclusion, say so in \"caveats\" and propose an experiment to settle it instead of a conclusion.");
        sb.AppendLine("6. \"nextPlan\": a goal for the next plan and suggested angles, each with why. They must be grounded in what the posts showed or, with thin evidence, be explicit experiments.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONE JSON object and nothing else: {\"headline\": string, \"confidence\": \"low\"|\"medium\"|\"high\", \"whatWorked\": [string], \"whatDidNot\": [string], \"learnings\": [string], \"nextPlan\": {\"goal\": string, \"suggestedAngles\": [{\"angle\": string, \"why\": string}]}, \"caveats\": [string]}");
        return sb.ToString();
    }

    public static string BuildReviewUser(GrowthPlan plan, IReadOnlyList<Measure> measures, IReadOnlyList<SlotFigures> figures, DataQuality q)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Plan: {plan.Name}");
        sb.AppendLine($"Goal: {plan.Goal}");
        sb.AppendLine($"Channel: {plan.Channel}. {plan.Days} day(s) from {plan.StartDate:yyyy-MM-dd}.");
        sb.AppendLine("Measures the plan said it would judge itself by: " + (measures.Count == 0 ? "(none recorded)" : string.Join("; ", measures.Select(m => $"{m.Name} ({m.Why})"))));
        sb.AppendLine($"Evidence: {q.Slots} posts planned, {q.Published} published, {q.Measured} with full engagement figures, {q.WithViews} with views.");
        sb.AppendLine();
        sb.AppendLine("Post | Angle | Published | Reactions | Comments | Shares | Engagements (sum) | Views | Reach | Link clicks");
        foreach (var f in figures)
            sb.AppendLine($"{f.Position} | {f.Angle} | {(f.PublishedAt is null ? "not published" : f.PublishedAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))} | {Cell(f.Reactions)} | {Cell(f.Comments)} | {Cell(f.Shares)} | {Cell(f.Engagements)} | {Cell(f.Views)} | {Cell(f.Reach)} | {Cell(f.Clicks)}");
        return sb.ToString();
    }

    public static ReviewResult? ParseReview(string text, out string? error)
    {
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) { error = "The answer was not a JSON object."; return null; }
        try { error = null; return JsonSerializer.Deserialize<ReviewResult>(text[start..(end + 1)], Json) ?? throw new JsonException("null"); }
        catch (JsonException ex) { error = "The answer was not valid JSON: " + ex.Message; return null; }
    }

    private static IEnumerable<string> Texts(ReviewResult r)
        => new[] { r.Headline }.Concat(r.WhatWorked).Concat(r.WhatDidNot).Concat(r.Learnings).Concat(r.Caveats).Append(r.NextPlan.Goal)
            .Concat(r.NextPlan.SuggestedAngles.SelectMany(a => new[] { a.Angle, a.Why }));

    /// <summary>Every figure the review quotes must be one it was given. Small whole numbers (post numbers, day counts) are allowed.</summary>
    public static List<string> ValidateReview(ReviewResult r, IReadOnlyList<SlotFigures> figures, DataQuality q)
    {
        var v = new List<string>();
        if (string.IsNullOrWhiteSpace(r.Headline)) v.Add("A headline is required.");
        if (r.Confidence is not ("low" or "medium" or "high")) v.Add("confidence must be low, medium or high.");
        if (q.Measured < 5 && r.Confidence != "low") v.Add("With fewer than 5 measured posts the confidence must be \"low\".");
        var known = new HashSet<decimal>();
        foreach (var f in figures)
            foreach (var n in new decimal?[] { f.Reactions, f.Comments, f.Shares, f.Engagements, f.Views, f.Reach, f.Clicks }) if (n is not null) known.Add(n.Value);
        foreach (var n in new[] { q.Slots, q.Published, q.Measured, q.WithViews }) known.Add(n);
        var invented = new HashSet<string>();
        foreach (var t in Texts(r))
            foreach (Match m in Regex.Matches(t ?? "", @"\d[\d,]*(?:\.\d+)?%?"))
            {
                var raw = m.Value.TrimEnd('%').Replace(",", "");
                if (m.Value.EndsWith('%')) { invented.Add(m.Value); continue; }   // a percentage is a computation the model was told not to make
                if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) continue;
                if (d <= 60 && d == decimal.Truncate(d)) continue;                  // post numbers, day counts
                if (!known.Contains(d)) invented.Add(m.Value);
            }
        if (invented.Count > 0) v.Add("These figures were not in the data you were given: " + string.Join(", ", invented) + ". Quote only given figures.");
        return v;
    }

    /// <summary>When there is nothing to analyse, say so plainly instead of asking a model to make something up.</summary>
    public static ReviewResult NotEnoughData(DataQuality q)
    {
        var why = q.Published == 0
            ? "None of this plan's posts has been published yet, so there are no results to review."
            : $"{q.Published} post(s) are published but the network has not reported figures for them yet (figures are pulled every 3 hours).";
        return new ReviewResult
        {
            Headline = "Not enough data to review this plan yet.", Confidence = "low",
            Caveats = [why, "Come back once posts have been live for a day or two and figures show in the table."],
        };
    }
}
