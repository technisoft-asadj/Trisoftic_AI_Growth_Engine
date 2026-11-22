using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Planning;

/// <summary>
/// The Instagram copy of an autopilot post. Made only after the Facebook post was approved and scheduled, from the same text and the same pictures or video.
/// A post that has no picture or video is skipped (Instagram has no text-only posts), and the reason is kept.
/// </summary>
public sealed class InstagramMirror : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid SlotId { get; set; }
    /// <summary>The Facebook post this one copies. One copy per post.</summary>
    public Guid SourceItemId { get; set; }
    public Guid? MirrorItemId { get; set; }
    /// <summary>Created (the copy exists) or Skipped (it was not made, see Note).</summary>
    public string Status { get; set; } = "Created";
    public string? Note { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The "also post to Instagram" step of the autopilot: copy each approved, scheduled Facebook post with media to Instagram, check the copy with the same fixed checks, approve it as the AI reviewer,
/// and schedule it an hour after the Facebook post. The copy gets its own tracked link so Instagram clicks are counted separately.
/// </summary>
public sealed class InstagramMirrorService(PlanningDbContext db, IContentPublishSource source, IContentPlanner content, IContentCatalog catalog, ITrackedLinks links, IChannelHealth channels,
    IAgentApprovals agent, IAssetCatalog assets, IProductKnowledge knowledge, IClock clock, IAuditWriter audit)
{
    public const string ChannelName = "Instagram";
    private const int MaxNewPerRun = 3;
    private const string Subject = "ContentItem";

    public async Task RunAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        if (!s.AlsoInstagram) return;
        if (!(await channels.ListAsync(s.ProductId, ct)).Any(c => string.Equals(c.Channel, ChannelName, StringComparison.OrdinalIgnoreCase) && c.Status == "Connected")) return;
        var k = await knowledge.GetApprovedAsync(s.ProductId, ct);
        if (k is null) return;
        await CopyAsync(s, notes, ct); db.ChangeTracker.Clear();
        await ReviewAsync(s, k, notes, ct); db.ChangeTracker.Clear();
        await ScheduleAsync(s, notes, ct);
    }

    private async Task CopyAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        var planIds = await db.Plans.Where(p => p.ProductId == s.ProductId && p.Managed && p.Status != PlanStatus.Cancelled && p.Status != PlanStatus.Completed).Select(p => p.Id).ToListAsync(ct);
        var slots = await db.Slots.AsNoTracking().Where(x => planIds.Contains(x.PlanId) && x.ContentItemId != null).OrderBy(x => x.ScheduledFor).ToListAsync(ct);
        var done = await db.Mirrors.AsNoTracking().Where(m => m.ProductId == s.ProductId).Select(m => m.SourceItemId).ToListAsync(ct);
        var todo = slots.Where(x => !done.Contains(x.ContentItemId!.Value)).ToList();
        if (todo.Count == 0) return;
        var statuses = (await content.GetStatusesAsync(todo.Select(x => x.ContentItemId!.Value).ToList(), ct)).ToDictionary(x => x.ItemId);

        var made = 0;
        foreach (var slot in todo)
        {
            if (made >= MaxNewPerRun) break;
            if (!statuses.TryGetValue(slot.ContentItemId!.Value, out var st) || st.Status != "Scheduled" || st.ApprovedVersion is not { } version) continue;   // only posts that passed the reviewer and are waiting to go out
            var c = await source.GetAsync(st.ItemId, version, ct);
            if (c is null) continue;
            var media = c.AssetIds is { Count: > 0 } ids ? await assets.GetAsync(ids, ct) : [];

            string? skip = null; var format = "Post";
            if (media.Count == 0) skip = "The Facebook post has no picture or video, and Instagram has no text-only posts.";
            else if (media.All(MediaRules.IsVideo) && media.Count == 1 && c.Format == "Reel") format = "Reel";
            else if (!media.All(MediaRules.IsImage)) skip = "The attached files cannot be posted to Instagram as they are.";

            var row = new InstagramMirror { ProductId = s.ProductId, SlotId = slot.Id, SourceItemId = st.ItemId, CreatedAt = clock.UtcNow };
            if (skip is not null) { row.Status = "Skipped"; row.Note = skip; db.Mirrors.Add(row); await db.SaveChangesAsync(ct); continue; }

            var body = c.Body; var linkIds = new List<Guid>();
            foreach (var url in AutopilotGates.UrlsIn(body).Distinct(StringComparer.OrdinalIgnoreCase))
                if ((await links.OwnShortUrlsAsync(s.ProductId, [url], ct)).Contains(url))
                    if (await links.RetargetAsync(s.ProductId, url, ChannelName, $"{c.Title} (Instagram)", ct) is { } own) { body = body.Replace(url, own.ShortUrl, StringComparison.OrdinalIgnoreCase); linkIds.Add(own.Id); }

            var title = c.Title.Length > 180 ? c.Title[..180] : c.Title;
            var itemId = await content.CreateAsync(new NewContent(s.ProductId, $"{title} (Instagram)", ChannelName, format, null, body, null, media.Select(m => m.Id).ToList(), slot.HookType), ct);
            await links.AttachAsync(linkIds, itemId, ct);
            var submitted = await content.SubmitAsync(itemId, ct);
            row.MirrorItemId = itemId; row.ScheduledFor = slot.ScheduledFor.AddHours(1);
            if (!submitted.Ok) row.Note = submitted.Error;
            db.Mirrors.Add(row); await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("autopilot.instagram-copy", "ContentItem", itemId.ToString(), s.ProductId, $"Copy of post {slot.Position}", After: new { source = st.ItemId, format, submitted = submitted.Ok }), ct);
            notes.Add($"Made the Instagram copy of post {slot.Position}." + (submitted.Ok ? "" : " It could not be submitted: " + submitted.Error));
            made++;
        }
    }

    // The copy has the same words and media as a post the AI reviewer already approved, so it gets the fixed checks again (links, claims, media for Instagram) and no second model call.
    private async Task ReviewAsync(AutopilotSettings s, ApprovedKnowledge k, List<string> notes, CancellationToken ct)
    {
        var mirrors = await db.Mirrors.AsNoTracking().Where(m => m.ProductId == s.ProductId && m.MirrorItemId != null).ToListAsync(ct);
        if (mirrors.Count == 0) return;
        var bySubject = mirrors.ToDictionary(m => m.MirrorItemId!.Value.ToString());
        var pending = await agent.ListPendingAsync(Subject, [.. bySubject.Keys], ct);
        if (pending.Count == 0) return;
        var recent = await catalog.GetRecentBodiesAsync(s.ProductId, ChannelName, clock.UtcNow.AddDays(-30), ct);
        var handled = await db.Decisions.AsNoTracking().Where(d => d.ProductId == s.ProductId && d.Verdict == "Escalated" && d.ContentItemId != null).Select(d => new { d.ContentItemId, d.Version }).ToListAsync(ct);
        foreach (var p in pending)
        {
            var m = bySubject[p.SubjectId]; var itemId = m.MirrorItemId!.Value;
            if (handled.Any(h => h.ContentItemId == itemId && h.Version == p.SubjectVersion)) continue;
            var c = await source.GetAsync(itemId, p.SubjectVersion, ct);
            if (c is null || c.Status != "InReview" || c.CurrentVersion != p.SubjectVersion) continue;
            var gates = AutopilotGates.Check(c.Body, k, recent.Where(b => b != c.Body).ToList(), await links.OwnShortUrlsAsync(s.ProductId, AutopilotGates.UrlsIn(c.Body), ct));
            var media = c.AssetIds is { Count: > 0 } ids ? await assets.GetAsync(ids, ct) : [];
            gates.AddRange(AutopilotGates.CheckMedia(c.Format, media, s.ProductId, clock.UtcNow, c.Body, null, null, ChannelName));
            var checks = gates.Select(g => new { g.Name, g.Passed, g.Detail }).Cast<object>().ToList();
            if (gates.All(g => g.Passed))
            {
                var reasons = "The same words and media as the Facebook post the AI reviewer approved; the fixed checks were repeated for Instagram and passed.";
                var d = await agent.DecideAsync(p.ApprovalId, ApprovalDecision.Approved, "Approved by the AI reviewer. " + reasons, ct);
                if (!d.IsSuccess) { notes.Add("Instagram copy: approval failed: " + d.Error!.Message); continue; }
                await LogAsync(s.ProductId, itemId, p.SubjectVersion, "Approved", reasons, checks, ct);
                notes.Add("Approved an Instagram copy.");
            }
            else
            {
                var reasons = "The Instagram copy failed a check: " + string.Join(" ", gates.Where(g => !g.Passed).Select(g => g.Detail));
                await LogAsync(s.ProductId, itemId, p.SubjectVersion, "Escalated", reasons, checks, ct);
                notes.Add("An Instagram copy is left for you: " + (reasons.Length > 120 ? reasons[..120] : reasons));
            }
        }
    }

    private async Task ScheduleAsync(AutopilotSettings s, List<string> notes, CancellationToken ct)
    {
        var mirrors = await db.Mirrors.Where(m => m.ProductId == s.ProductId && m.MirrorItemId != null).ToListAsync(ct);
        if (mirrors.Count == 0) return;
        var statuses = (await content.GetStatusesAsync(mirrors.Select(m => m.MirrorItemId!.Value).ToList(), ct)).ToDictionary(x => x.ItemId);
        var now = clock.UtcNow; var scheduled = 0;
        foreach (var m in mirrors.OrderBy(m => m.ScheduledFor))
        {
            if (!statuses.TryGetValue(m.MirrorItemId!.Value, out var st) || st.Status != "Approved" || st.ApprovedVersion != st.CurrentVersion) continue;
            var at = m.ScheduledFor is { } t && t > now.AddMinutes(5) ? t : now.AddMinutes(10);
            if (await catalog.CountOnDayAsync(s.ProductId, ChannelName, DateOnly.FromDateTime(at.UtcDateTime), ct) >= s.MaxPostsPerDay) continue;   // the daily limit applies to Instagram too; try again later
            var r = await content.ScheduleAsync(st.ItemId, at, ct);
            if (r.Ok) scheduled++; else notes.Add("An Instagram copy could not be scheduled: " + r.Error);
        }
        if (scheduled > 0) notes.Add($"Scheduled {scheduled} Instagram post(s).");
    }

    private async Task LogAsync(Guid productId, Guid itemId, int version, string verdict, string reasons, IReadOnlyList<object> checks, CancellationToken ct)
    {
        db.Decisions.Add(new AutopilotDecision
        {
            ProductId = productId, ContentItemId = itemId, Version = version, Verdict = verdict, Attempt = 1, Reasons = reasons.Length > 4000 ? reasons[..4000] : reasons,
            ChecksJson = JsonSerializer.Serialize(checks, PlanLogic.Json), Model = "", CostUsd = 0, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("autopilot." + verdict.ToLowerInvariant(), "ContentItem", itemId.ToString(), productId, reasons.Length > 200 ? reasons[..200] : reasons, After: new { version, channel = ChannelName }), ct);
    }
}
