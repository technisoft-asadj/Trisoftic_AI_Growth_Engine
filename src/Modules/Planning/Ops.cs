using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Planning;

/// <summary>A problem the owner should know about. Open until the cause goes away, then resolved by itself.</summary>
public sealed class OwnerAlert : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>Stable name of the problem, such as "account:Ludo Fight" or "ai-budget". One open alert per key.</summary>
    public string Key { get; set; } = "";
    /// <summary>warning or critical.</summary>
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? EmailedAt { get; set; }
}

/// <summary>The weekly report written for the owner. Plain text, built from recorded figures only.</summary>
public sealed class OwnerDigest : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EmailedAt { get; set; }
}

public sealed record AlertCandidate(string Key, string Severity, string Message);

/// <summary>What the alert rules look at. Built from the other modules' contracts.</summary>
public sealed record OpsSnapshot(
    DateTimeOffset Now, bool AutopilotEnabled, string Channel, DateTimeOffset? LastRunAt, TimeSpan LoopEvery,
    IReadOnlyList<ChannelHealth> Accounts, AiSpend Spend, int NeedsYou, int FailedPosts, DateTimeOffset? LastPublishedAt);

/// <summary>The alert rules. Pure, so every threshold is tested.</summary>
public static class OwnerAlerts
{
    public static IReadOnlyList<AlertCandidate> Evaluate(OpsSnapshot s)
    {
        var found = new List<AlertCandidate>();
        if (!s.AutopilotEnabled) return found;

        var matching = s.Accounts.Where(a => string.Equals(a.Channel, s.Channel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0) found.Add(new("no-account", "critical", $"No {s.Channel} account is connected, so nothing can be posted."));
        foreach (var a in matching)
        {
            if (a.Status != "Connected") found.Add(new($"account:{a.Name}", "critical", $"{a.Name} needs to be reconnected, so posting has stopped." + (string.IsNullOrWhiteSpace(a.LastError) ? "" : " Last error: " + Short(a.LastError, 200))));
            else if (a.TokenExpiresAt is { } exp)
            {
                if (exp <= s.Now) found.Add(new($"account:{a.Name}", "critical", $"The connection to {a.Name} has expired. Reconnect it."));
                else if (exp <= s.Now.AddDays(7)) found.Add(new($"account:{a.Name}", "warning", $"The connection to {a.Name} expires on {exp:yyyy-MM-dd}. Reconnect it before then."));
            }
        }

        var p = s.Spend;
        if (p.ProductBudgetUsd > 0 && p.ProductUsd >= p.ProductBudgetUsd) found.Add(new("ai-budget", "critical", $"This product's monthly AI budget is used up (${p.ProductUsd:F2} of ${p.ProductBudgetUsd:F2}). The autopilot cannot write or review until next month or the limit is raised."));
        else if (p.ProductBudgetUsd > 0 && p.ProductUsd >= p.ProductBudgetUsd * p.AlertThreshold) found.Add(new("ai-budget", "warning", $"This product has used ${p.ProductUsd:F2} of its ${p.ProductBudgetUsd:F2} monthly AI budget."));
        if (p.GlobalBudgetUsd > 0 && p.GlobalUsd >= p.GlobalBudgetUsd) found.Add(new("ai-budget-global", "critical", $"The overall monthly AI budget is used up (${p.GlobalUsd:F2} of ${p.GlobalBudgetUsd:F2})."));
        else if (p.GlobalBudgetUsd > 0 && p.GlobalUsd >= p.GlobalBudgetUsd * p.AlertThreshold) found.Add(new("ai-budget-global", "warning", $"The overall AI spend is ${p.GlobalUsd:F2} of the ${p.GlobalBudgetUsd:F2} monthly budget."));

        var stall = TimeSpan.FromTicks(Math.Max(s.LoopEvery.Ticks * 4, TimeSpan.FromMinutes(20).Ticks));
        if (s.LastRunAt is { } last && last < s.Now - stall) found.Add(new("autopilot-stalled", "critical", $"The autopilot has not run since {last:yyyy-MM-dd HH:mm} UTC. The server may be down."));

        if (s.NeedsYou > 0) found.Add(new("needs-you", "warning", $"{s.NeedsYou} post(s) are waiting for your decision. The AI reviewer was not sure about them."));
        if (s.FailedPosts > 0) found.Add(new("failed-posts", "warning", $"{s.FailedPosts} post(s) failed to publish."));
        if (s.LastPublishedAt is { } lp && lp < s.Now.AddDays(-3) && matching.Any(a => a.Status == "Connected"))
            found.Add(new("no-posts", "warning", $"Nothing has been published for {(int)(s.Now - lp).TotalDays} days although the autopilot is on."));
        return found;
    }

    private static string Short(string v, int n) => v.Length <= n ? v : v[..n];
}

public sealed record DigestPost(string Title, DateTimeOffset PublishedAt, int? Reactions, int? Comments, int? Shares, long? Views, long? Reach, string? Url)
{
    /// <summary>Reactions, comments and shares added up. Null when the network gave none of them (never zero for "unknown").</summary>
    public int? Engagements => Reactions is null && Comments is null && Shares is null ? null : (Reactions ?? 0) + (Comments ?? 0) + (Shares ?? 0);
}

public sealed record DigestInput(
    DateTimeOffset From, DateTimeOffset To, IReadOnlyList<DigestPost> Posts, int Approved, int Rejected, int Escalated, decimal ReviewCostUsd,
    IReadOnlyList<string> Supported, IReadOnlyList<string> Contradicted, int OpenLearnings, int NeedsYou, IReadOnlyList<string> Alerts, decimal AiMonthUsd, decimal AiMonthBudgetUsd);

/// <summary>Builds the weekly digest from recorded figures. No AI call, so it costs nothing and cannot invent anything.</summary>
public static class DigestWriter
{
    public static string Subject(DigestInput d) => $"Weekly growth digest: {d.From:d MMM} to {d.To:d MMM}";

    public static string Write(DigestInput d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Your week, {d.From:d MMM} to {d.To:d MMM yyyy}");
        sb.AppendLine();
        var measured = d.Posts.Count(p => p.Engagements is not null);
        sb.AppendLine(d.Posts.Count == 0 ? "No posts went out this week." : $"{d.Posts.Count} post(s) went out; {measured} have figures from the network yet.");
        foreach (var p in d.Posts.OrderBy(p => p.PublishedAt))
            sb.AppendLine($"  - {p.PublishedAt:ddd d MMM}: {p.Title}: {Figures(p)}");
        var best = d.Posts.Where(p => p.Engagements is not null).OrderByDescending(p => p.Engagements).FirstOrDefault();
        if (best is not null && measured >= 2) sb.AppendLine($"Strongest: \"{best.Title}\" with {best.Engagements} reactions, comments and shares together.");
        sb.AppendLine();
        var checks = d.Approved + d.Rejected + d.Escalated;
        sb.AppendLine(checks == 0 ? "The AI reviewer had nothing to check." : $"The AI reviewer checked {checks} post(s): {d.Approved} approved, {d.Rejected} sent back for a rewrite, {d.Escalated} left for you. Review cost this week: ${d.ReviewCostUsd:F2}.");
        if (d.NeedsYou > 0) sb.AppendLine($"Waiting for you: {d.NeedsYou} post(s). Open the Autopilot page to decide.");
        sb.AppendLine();
        if (d.Supported.Count + d.Contradicted.Count == 0) sb.AppendLine($"Learnings: nothing was settled this week. {d.OpenLearnings} idea(s) are still being tested.");
        else
        {
            sb.AppendLine("Learnings this week:");
            foreach (var s in d.Supported) sb.AppendLine($"  - Supported: {s}");
            foreach (var s in d.Contradicted) sb.AppendLine($"  - Contradicted: {s}");
            sb.AppendLine($"{d.OpenLearnings} idea(s) are still being tested.");
        }
        sb.AppendLine();
        if (d.Alerts.Count > 0) { sb.AppendLine("Open problems:"); foreach (var a in d.Alerts) sb.AppendLine("  - " + a); }
        else sb.AppendLine("Open problems: none.");
        sb.AppendLine();
        sb.AppendLine($"AI spend this month: ${d.AiMonthUsd:F2} of ${d.AiMonthBudgetUsd:F2} for this product.");
        return sb.ToString().TrimEnd();
    }

    private static string Figures(DigestPost p)
    {
        string F(string name, long? v) => v is null ? $"{name} not reported" : $"{v} {name}";
        return $"{F("reactions", p.Reactions)}, {F("comments", p.Comments)}, {F("shares", p.Shares)}" + (p.Views is null ? "" : $", {p.Views} views");
    }
}

/// <summary>Checks for problems and writes the weekly digest. Runs after each autopilot pass, and on request.</summary>
public sealed class OpsService(PlanningDbContext db, IChannelHealth channels, IAiSpendReader spend, IContentCatalog catalog, IContentPublishSource source, IContentPlanner content,
    IPostPerformanceReader perf, IMessageOutbox outbox, IConfiguration cfg, IClock clock)
{
    public const int DigestEveryDays = 7;

    /// <summary>Recomputes the alerts: opens new ones, refreshes those still true, resolves those that are gone, and emails the owner about new ones.</summary>
    public async Task CheckAlertsAsync(Guid productId, CancellationToken ct)
    {
        var s = await db.Autopilot.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == productId, ct);
        if (s is null) return;
        var now = clock.UtcNow;
        var published = await catalog.GetPublishedAsync(productId, s.Channel, ct);
        var snap = new OpsSnapshot(now, s.Enabled, s.Channel, s.LastRunAt, TimeSpan.FromSeconds(Math.Max(30, cfg.GetValue("Autopilot:IntervalSeconds", 300))),
            await channels.ListAsync(productId, ct), await spend.GetAsync(productId, ct), await NeedsYouAsync(productId, ct),
            (await source.GetFailedAsync([productId], ct)).Count, published.Count == 0 ? null : published.Max(p => p.PublishedAt));
        var wanted = OwnerAlerts.Evaluate(snap).ToDictionary(a => a.Key);

        var open = await db.Alerts.Where(a => a.ProductId == productId && a.ResolvedAt == null).ToListAsync(ct);
        foreach (var a in open)
        {
            if (wanted.Remove(a.Key, out var c)) { a.Message = c.Message; a.Severity = c.Severity; a.LastSeenAt = now; }
            else a.ResolvedAt = now;
        }
        var fresh = wanted.Values.Select(c => new OwnerAlert { ProductId = productId, Key = c.Key, Severity = c.Severity, Message = c.Message, OpenedAt = now, LastSeenAt = now }).ToList();
        db.Alerts.AddRange(fresh);
        await db.SaveChangesAsync(ct);

        foreach (var a in fresh) await TryEmailAsync(a, ct);
    }

    private async Task TryEmailAsync(OwnerAlert a, CancellationToken ct)
    {
        var to = cfg["Ops:OwnerEmail"];
        if (string.IsNullOrWhiteSpace(to)) return;
        await outbox.QueueEmailAsync(new EmailRequest(ContactPurpose.Transactional, $"{(a.Severity == "critical" ? "Needs attention" : "Heads up")}: {Trim(a.Message, 80)}", a.Message + "\n\nOpen the Autopilot page for details.",
            DedupKey: $"ops-alert:{a.Id}", ToAddress: to.Trim(), ProductId: a.ProductId, SubjectType: "OwnerAlert", SubjectId: a.Id), ct);
        a.EmailedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Writes the digest when a week has passed since the last one (or since the autopilot first did something). Returns it, or null when none is due.</summary>
    public async Task<OwnerDigest?> DigestIfDueAsync(Guid productId, CancellationToken ct)
    {
        var s = await db.Autopilot.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == productId, ct);
        if (s is null || !s.Enabled) return null;
        var now = clock.UtcNow;
        var last = await db.Digests.Where(d => d.ProductId == productId).MaxAsync(d => (DateTimeOffset?)d.PeriodEnd, ct);
        var since = last ?? await db.Decisions.Where(d => d.ProductId == productId).MinAsync(d => (DateTimeOffset?)d.CreatedAt, ct);
        if (since is null || since > now.AddDays(-DigestEveryDays)) return null;
        var weekAgo = now.AddDays(-DigestEveryDays);
        return await WriteDigestAsync(productId, s.Channel, last is { } l && l > weekAgo ? l : weekAgo, now, ct);
    }

    public async Task<OwnerDigest> WriteDigestAsync(Guid productId, string channel, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var published = (await catalog.GetPublishedAsync(productId, channel, ct)).Where(p => p.PublishedAt >= from && p.PublishedAt <= to).ToList();
        var ids = published.Select(p => p.ItemId).ToList();
        var titles = (await content.GetStatusesAsync(ids, ct)).ToDictionary(x => x.ItemId, x => x.Title);
        var figures = (await perf.GetAsync(ids, ct)).ToDictionary(x => x.ContentItemId);
        var posts = published.Select(p =>
        {
            figures.TryGetValue(p.ItemId, out var f);
            return new DigestPost(titles.GetValueOrDefault(p.ItemId) ?? "Post", p.PublishedAt, f?.Reactions, f?.Comments, f?.Shares, f?.Views, f?.Reach, f?.Url);
        }).ToList();

        var decisions = await db.Decisions.AsNoTracking().Where(d => d.ProductId == productId && d.CreatedAt >= from && d.CreatedAt <= to).ToListAsync(ct);
        var learnings = await db.Learnings.AsNoTracking().Where(l => l.ProductId == productId).ToListAsync(ct);
        var week = learnings.Where(l => l.UpdatedAt >= from && l.UpdatedAt <= to).ToList();
        var alerts = await db.Alerts.AsNoTracking().Where(a => a.ProductId == productId && a.ResolvedAt == null).OrderBy(a => a.OpenedAt).Select(a => a.Message).ToListAsync(ct);
        var sp = await spend.GetAsync(productId, ct);
        var input = new DigestInput(from, to, posts, decisions.Count(d => d.Verdict == "Approved"), decisions.Count(d => d.Verdict == "Rejected"), decisions.Count(d => d.Verdict == "Escalated"),
            decisions.Sum(d => d.CostUsd), week.Where(l => l.Status == LearningStatus.Supported).Select(l => l.Statement).ToList(), week.Where(l => l.Status == LearningStatus.Contradicted).Select(l => l.Statement).ToList(),
            learnings.Count(l => l.Status == LearningStatus.Open), await NeedsYouAsync(productId, ct), alerts, sp.ProductUsd, sp.ProductBudgetUsd);

        var d = new OwnerDigest { ProductId = productId, PeriodStart = from, PeriodEnd = to, Subject = DigestWriter.Subject(input), Body = DigestWriter.Write(input), CreatedAt = clock.UtcNow };
        db.Digests.Add(d);
        await db.SaveChangesAsync(ct);
        if (cfg["Ops:OwnerEmail"] is { Length: > 0 } email && !string.IsNullOrWhiteSpace(email))
        {
            await outbox.QueueEmailAsync(new EmailRequest(ContactPurpose.Transactional, d.Subject, d.Body, DedupKey: $"ops-digest:{d.Id}", ToAddress: email.Trim(), ProductId: productId, SubjectType: "OwnerDigest", SubjectId: d.Id), ct);
            d.EmailedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return d;
    }

    private async Task<int> NeedsYouAsync(Guid productId, CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-14);
        var escalated = await db.Decisions.AsNoTracking().Where(d => d.ProductId == productId && d.Verdict == "Escalated" && d.ContentItemId != null && d.CreatedAt >= since).ToListAsync(ct);
        if (escalated.Count == 0) return 0;
        var statuses = (await content.GetStatusesAsync(escalated.Select(d => d.ContentItemId!.Value).Distinct().ToList(), ct)).ToDictionary(x => x.ItemId);
        return escalated.Where(d => statuses.TryGetValue(d.ContentItemId!.Value, out var st) && st.Status == "InReview" && st.CurrentVersion == d.Version).Select(d => d.ContentItemId).Distinct().Count();
    }

    private static string Trim(string v, int n) => v.Length <= n ? v : v[..n];
}

public static class OpsEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/products/{productId:guid}/ops", async (Guid productId, PlanningDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var alerts = await db.Alerts.AsNoTracking().Where(a => a.ProductId == productId && a.ResolvedAt == null).OrderByDescending(a => a.Severity).ThenBy(a => a.OpenedAt)
                .Select(a => new { a.Id, a.Key, a.Severity, a.Message, a.OpenedAt }).ToListAsync(ct);
            var digests = await db.Digests.AsNoTracking().Where(d => d.ProductId == productId).OrderByDescending(d => d.CreatedAt).Take(8)
                .Select(d => new { d.Id, d.Subject, d.Body, d.PeriodStart, d.PeriodEnd, d.CreatedAt, emailed = d.EmailedAt != null }).ToListAsync(ct);
            return Results.Ok(new { alerts, digests });
        });

        g.MapPost("/products/{productId:guid}/ops/check", async (Guid productId, PlanningDbContext db, OpsService ops, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            await ops.CheckAlertsAsync(productId, ct);
            var digest = await ops.DigestIfDueAsync(productId, ct);
            return Results.Ok(new { digestWritten = digest is not null });
        });

        g.MapPost("/products/{productId:guid}/ops/digest", async (Guid productId, PlanningDbContext db, OpsService ops, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var s = await db.Autopilot.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == productId, ct);
            if (s is null) return Results.Problem(statusCode: 409, title: "Set up the autopilot first.");
            var now = clock.UtcNow;
            var d = await ops.WriteDigestAsync(productId, s.Channel, now.AddDays(-OpsService.DigestEveryDays), now, ct);
            return Results.Ok(new { d.Id, d.Subject, d.Body });
        });
    }
}
