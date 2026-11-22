using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Publishing;

public sealed class TokenVault(IDataProtectionProvider dp)
{
    private readonly IDataProtector _p = dp.CreateProtector("TriSoftic.GrowthEngine.Publishing.Tokens.v1");
    public string Seal(string plain) => _p.Protect(plain);
    public string Open(string cipher) => _p.Unprotect(cipher);
}

/// <summary>
/// Turns a due, approved, scheduled content version into a confirmed post.
/// The guarantees: only the approved current version goes out; "Published" is set only after the provider returned a post id;
/// a retry never creates a second post (one PublishedPost per version, and an uncertain earlier attempt is checked against the provider first).
/// </summary>
public sealed class PublishPipeline(PublishingDbContext db, IContentPublishSource content, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IAssetCatalog catalog, IClock clock, IAuditWriter audit)
{
    public const string PublishJob = "publishing.publish";

    public async Task RunAsync(Guid itemId, int version, bool lastAttempt, CancellationToken ct)
    {
        // Already live (a redelivered job, or a manual "published" record): make sure the content agrees and stop.
        if (await db.Posts.AnyAsync(p => p.ContentItemId == itemId && p.Version == version, ct)) { await content.CompletePublishAsync(itemId, version, clock.UtcNow, ct); return; }

        var item = await content.GetAsync(itemId, version, ct);
        if (item is null) return;
        // Edited, unscheduled or cancelled since the job was queued: nothing to do. The approval only ever covered the exact version.
        if (item.Status is not ("Scheduled" or "Publishing") || item.ApprovedVersion != version || item.CurrentVersion != version) return;

        var account = await db.Accounts.Where(a => a.ProductId == item.ProductId && a.Status != AccountStatus.Disconnected && a.Channel.ToLower() == item.Channel.ToLower()).OrderBy(a => a.ConnectedAt).FirstOrDefaultAsync(ct);
        if (account is null) { await FailAsync(item, $"No account is connected for the channel '{item.Channel}'. Connect one, then retry."); return; }
        if (account.Status != AccountStatus.Connected) { await FailAsync(item, $"The {item.Channel} account '{account.DisplayName}' needs to be reconnected. Reconnect it, then retry."); return; }
        var publisher = publishers.FirstOrDefault(p => p.Provider == account.Provider);
        if (publisher is null) { await FailAsync(item, $"No adapter is available for '{account.Provider}'."); return; }
        if (!publisher.SupportedFormats.Contains(item.Format)) { await FailAsync(item, $"'{item.Format}' content cannot be published to {item.Channel} yet (supported: {string.Join(", ", publisher.SupportedFormats)})."); return; }
        if (string.IsNullOrWhiteSpace(item.Body) && item.Format != "Story") { await FailAsync(item, "The approved text is empty."); return; }   // photos and videos need their caption too: it is how a retry recognises the post

        // Files: checked again at the last moment (a file may have been retired or its rights may have expired since approval), then opened lazily so a video is streamed.
        var assetIds = item.AssetIds ?? [];
        var media = new List<MediaFile>();
        if (assetIds.Count > 0 || item.Format == "Story" || MediaRules.IsInstagram(item.Channel))
        {
            var infos = assetIds.Count == 0 ? [] : await catalog.GetAsync(assetIds, ct);
            if (assetIds.Any(i => infos.All(x => x.Id != i))) { await FailAsync(item, "An attached file no longer exists."); return; }
            var problems = MediaRules.Validate(item.Format, infos, item.ProductId, clock.UtcNow, complete: true, item.Channel);
            if (problems.Count > 0) { await FailAsync(item, "The attached files cannot be sent: " + string.Join(" ", problems)); return; }
            media = [.. assetIds.Select(id => infos.First(x => x.Id == id)).Select(i => new MediaFile(i.Name, i.ContentType, i.SizeBytes, async c => await catalog.OpenAsync(i.Id, c) ?? throw new PublishException(PublishErrorKind.Permanent, $"The file '{i.Name}' is missing from storage.")))];
        }
        if (item.Body.Length > publisher.MaxTextLength) { await FailAsync(item, $"The text is {item.Body.Length} characters; {item.Channel} allows {publisher.MaxTextLength}."); return; }

        string token;
        try { token = vault.Open(account.TokenCipher); }
        catch (System.Security.Cryptography.CryptographicException) { await FailAsync(item, "The stored account permission can no longer be read. Reconnect the account."); return; }

        var claim = await content.BeginPublishAsync(itemId, version, ct);
        if (!claim.Ok) return;   // lost a race with an edit/unschedule: leave it alone

        var req = new PostRequest(account.ExternalId, token, item.Body, media, item.Format);
        var prior = await db.Attempts.Where(a => a.ContentItemId == itemId && a.Version == version).OrderByDescending(a => a.Number).FirstOrDefaultAsync(ct);

        // An earlier try may have reached the provider without us hearing back. Look before sending again, or a duplicate is possible.
        if (prior is { Status: AttemptStatus.Unknown or AttemptStatus.Started })
        {
            PostResult? found = null;
            try { found = await publisher.FindRecentAsync(req, ct); }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable or PublishErrorKind.Unknown)
            {
                await Backoff(item, prior, $"Could not verify the earlier attempt: {ex.Message}", ex.RetryAfter, lastAttempt, ct);   // never send blind
            }
            catch (PublishException ex) { await HandleTerminal(item, account, prior, ex, ct); return; }
            if (found is not null) { await ConfirmAsync(item, account, prior, found, ct); return; }
        }

        var attempt = new PublishingAttempt { ContentItemId = itemId, Version = version, AccountId = account.Id, Number = (prior?.Number ?? 0) + 1, Status = AttemptStatus.Started, StartedAt = clock.UtcNow };
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync(ct);   // written before the call, so a crash mid-call leaves a trace and the next try verifies first

        PostResult result;
        try { result = await publisher.PublishAsync(req, ct); }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable)
        {
            await Backoff(item, attempt, ex.Message, ex.RetryAfter, lastAttempt, ct, AttemptStatus.Failed);
            return;
        }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.Unknown)
        {
            await Backoff(item, attempt, ex.Message, ex.RetryAfter, lastAttempt, ct, AttemptStatus.Unknown);
            return;
        }
        catch (PublishException ex) { await HandleTerminal(item, account, attempt, ex, ct); return; }

        await ConfirmAsync(item, account, attempt, result, ct);
    }

    private async Task ConfirmAsync(PublishableContent item, ChannelAccount account, PublishingAttempt? attempt, PostResult result, CancellationToken ct)
    {
        var now = clock.UtcNow;
        db.Posts.Add(new PublishedPost { ContentItemId = item.ItemId, Version = item.Version, ProductId = item.ProductId, AccountId = account.Id, ProviderPostId = result.ProviderPostId, Url = result.Url, Source = PostSource.Provider, PublishedAt = now });
        if (attempt is not null) { attempt.Status = AttemptStatus.Succeeded; attempt.FinishedAt = now; attempt.Error = null; }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { db.ChangeTracker.Clear(); }   // already recorded
        await content.CompletePublishAsync(item.ItemId, item.Version, now, ct);
        await audit.WriteAsync(new AuditEntry("publishing.published", "ContentItem", item.ItemId.ToString(), item.ProductId, item.Title, After: new { item.Version, result.ProviderPostId, result.Url, account.Provider }), ct);
    }

    private async Task Backoff(PublishableContent item, PublishingAttempt attempt, string message, TimeSpan? retryAfter, bool lastAttempt, CancellationToken ct, AttemptStatus status = AttemptStatus.Unknown)
    {
        attempt.Status = status; attempt.Error = message; attempt.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        if (lastAttempt)
        {
            var hint = status == AttemptStatus.Unknown ? " The provider did not confirm either way: check the page, then use 'mark as published' if the post is there, or retry (the retry checks first)." : "";
            await FailAsync(item, message + " Giving up after repeated attempts." + hint);
        }
        throw new TransientJobException(message, retryAfter);
    }

    private async Task HandleTerminal(PublishableContent item, ChannelAccount account, PublishingAttempt attempt, PublishException ex, CancellationToken ct)
    {
        attempt.Status = AttemptStatus.Failed; attempt.Error = ex.Message; attempt.FinishedAt = clock.UtcNow;
        if (ex.Kind == PublishErrorKind.AuthExpired) { account.Status = AccountStatus.NeedsReconnect; account.LastError = ex.Message; }
        await db.SaveChangesAsync(ct);
        await FailAsync(item, ex.Kind == PublishErrorKind.AuthExpired ? $"The {item.Channel} account must be reconnected: {ex.Message}" : $"{item.Channel} refused the post: {ex.Message}");
    }

    private async Task FailAsync(PublishableContent item, string reason)
    {
        await content.FailPublishAsync(item.ItemId, item.Version, reason, CancellationToken.None);
        await audit.WriteAsync(new AuditEntry("publishing.failed", "ContentItem", item.ItemId.ToString(), item.ProductId, item.Title, After: new { item.Version, reason }));
    }
}

public sealed class PublishJobHandler(PublishPipeline pipeline) : IJobHandler
{
    public string Type => PublishPipeline.PublishJob;
    public PauseScope? PauseScope => BuildingBlocks.Abstractions.PauseScope.Publishing;

    public Task HandleAsync(JobContext c, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(c.PayloadJson);
        return pipeline.RunAsync(doc.RootElement.GetProperty("itemId").GetGuid(), doc.RootElement.GetProperty("version").GetInt32(), c.Attempt >= c.MaxAttempts, ct);
    }
}

/// <summary>Finds scheduled content whose time has come and queues one publish job per item version.</summary>
public sealed class DispatchHandler(IContentPublishSource content, IJobQueue jobs, IClock clock) : IJobHandler
{
    public const string DispatchJob = "publishing.dispatch";
    public string Type => DispatchJob;
    public PauseScope? PauseScope => BuildingBlocks.Abstractions.PauseScope.Publishing;   // while paused, scheduled items simply wait

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        foreach (var due in await content.GetDueAsync(clock.UtcNow, 100, ct))
            await jobs.EnqueueAsync(new JobRequest(PublishPipeline.PublishJob, JsonSerializer.Serialize(new { itemId = due.ItemId, version = due.Version }),
                IdempotencyKey: $"pub:{due.ItemId}:{due.Version}:{due.ScheduledFor.UtcTicks}", ProductId: due.ProductId, MaxAttempts: 4), ct);
    }
}

/// <summary>Checks that each connected account is still accepted by its provider and flags the ones that are not.</summary>
public sealed class TokenCheckHandler(PublishingDbContext db, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IClock clock) : IJobHandler
{
    public const string CheckJob = "publishing.token-check";
    public string Type => CheckJob;
    public PauseScope? PauseScope => null;   // a read-only check

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        foreach (var a in await db.Accounts.Where(a => a.Status == AccountStatus.Connected).ToListAsync(ct))
        {
            var publisher = publishers.FirstOrDefault(p => p.Provider == a.Provider);
            if (publisher is null) continue;
            try
            {
                var ok = await publisher.IsAccountValidAsync(a.ExternalId, vault.Open(a.TokenCipher), ct);
                a.LastCheckedAt = clock.UtcNow;
                if (!ok) { a.Status = AccountStatus.NeedsReconnect; a.LastError = "The provider no longer accepts this account's permission"; }
            }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable or PublishErrorKind.Unknown) { a.LastError = "Last check could not complete: " + ex.Message; }
            catch (System.Security.Cryptography.CryptographicException) { a.Status = AccountStatus.NeedsReconnect; a.LastError = "The stored permission can no longer be read"; }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Queues a dispatch every minute and a token check every hour. Idempotency keys per time bucket make several hosts running it harmless.</summary>
public sealed class PublishingScheduler(IServiceScopeFactory scopes, IClock clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                var t = clock.UtcNow.ToUnixTimeSeconds();
                await jobs.EnqueueAsync(new JobRequest(DispatchHandler.DispatchJob, "{}", IdempotencyKey: $"dispatch:{t / 60}"), stoppingToken);
                await jobs.EnqueueAsync(new JobRequest(TokenCheckHandler.CheckJob, "{}", IdempotencyKey: $"tokencheck:{t / 3600}"), stoppingToken);
                await jobs.EnqueueAsync(new JobRequest(PullInsightsHandler.PullJob, "{}", IdempotencyKey: $"insights:{t / 21600}"), stoppingToken);
                await jobs.EnqueueAsync(new JobRequest(PullPostMetricsHandler.PullJob, "{}", IdempotencyKey: $"postmetrics:{t / 10800}"), stoppingToken);   // every 3 hours
                await jobs.EnqueueAsync(new JobRequest(PullPageFiguresHandler.PullJob, "{}", IdempotencyKey: $"pagefigures:{t / 21600}"), stoppingToken);   // every 6 hours
                await jobs.EnqueueAsync(new JobRequest(PullCommentsHandler.PullJob, "{}", IdempotencyKey: $"comments:{t / 1800}"), stoppingToken);   // every 30 minutes
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { /* next minute */ }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The publishing module's side of the growth-plan contract: the latest stored figures for each published post.</summary>
public sealed class PostPerformanceReader(PublishingDbContext db) : IPostPerformanceReader
{
    public async Task<IReadOnlyList<PostPerformance>> GetAsync(IReadOnlyCollection<Guid> contentItemIds, CancellationToken ct = default)
    {
        var posts = await db.Posts.AsNoTracking().Where(p => contentItemIds.Contains(p.ContentItemId)).ToListAsync(ct);
        var ids = posts.Select(p => p.Id).ToList();
        var latest = (await db.Snapshots.AsNoTracking().Where(s => ids.Contains(s.PublishedPostId)).ToListAsync(ct))
            .GroupBy(s => s.PublishedPostId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.TakenAt).First());
        return posts.Select(p =>
        {
            latest.TryGetValue(p.Id, out var s);
            return new PostPerformance(p.ContentItemId, p.PublishedAt, p.Url, s?.TakenAt, s?.Reactions, s?.Comments, s?.Shares, s?.Views, s?.Reach, s?.Clicks, s?.Note);
        }).ToList();
    }
}

/// <summary>
/// Asks the network how each recently published post is doing and stores a snapshot. Posts older than <see cref="WindowDays"/> are left alone.
/// The same figures are summed into analytics per publish day (engagements, impressions, clicks), but only when every post of that day has the figure,
/// so a missing number is never counted as zero.
/// </summary>
public sealed class PullPostMetricsHandler(PublishingDbContext db, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IClock clock, IMetricSink? sink = null) : IJobHandler
{
    public const string PullJob = "publishing.pull-post-metrics";
    public const int WindowDays = 30;
    public string Type => PullJob;
    public PauseScope? PauseScope => null;   // read-only

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var since = now.AddDays(-WindowDays);
        var posts = await db.Posts.Where(p => p.Source == PostSource.Provider && p.AccountId != null && p.PublishedAt >= since).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.Status == AccountStatus.Connected).ToDictionaryAsync(a => a.Id, ct);
        TransientJobException? failure = null;
        var touchedProducts = new HashSet<Guid>();
        foreach (var post in posts)
        {
            if (!accounts.TryGetValue(post.AccountId!.Value, out var account)) continue;
            var publisher = publishers.FirstOrDefault(p => p.Provider == account.Provider);
            if (publisher is null) continue;
            try
            {
                var m = await publisher.GetPostMetricsAsync(new PostRequest(account.ExternalId, vault.Open(account.TokenCipher), ""), post.ProviderPostId, ct);
                if (m is null) continue;   // this adapter cannot report on posts
                db.Snapshots.Add(new PostMetricSnapshot { PublishedPostId = post.Id, ProductId = post.ProductId, TakenAt = now, Reactions = m.Reactions, Comments = m.Comments, Shares = m.Shares, Views = m.Views, Reach = m.Reach, Clicks = m.Clicks, Note = m.Note });
                await db.SaveChangesAsync(ct);
                touchedProducts.Add(post.ProductId);
            }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable or PublishErrorKind.Unknown) { failure ??= new TransientJobException($"Post figures for '{account.DisplayName}': {ex.Message}", ex.RetryAfter); }
            catch (PublishException ex)
            {
                // Auth or permanent: nothing can be measured. Keep the reason with the post (all figures empty) so the page can say why; the token check flags a bad account.
                db.Snapshots.Add(new PostMetricSnapshot { PublishedPostId = post.Id, ProductId = post.ProductId, TakenAt = now, Note = Truncate("Facebook refused the request for this post's figures: " + ex.Message, 500) });
                await db.SaveChangesAsync(ct);
            }
            catch (System.Security.Cryptography.CryptographicException) { /* unreadable token: flagged by the token check */ }
        }
        if (sink is not null) foreach (var productId in touchedProducts) await RollUpAsync(productId, posts.Where(p => p.ProductId == productId).ToList(), accounts, now, ct);
        if (failure is not null) throw failure;
    }

    private async Task RollUpAsync(Guid productId, List<PublishedPost> posts, IReadOnlyDictionary<Guid, ChannelAccount> accounts, DateTimeOffset now, CancellationToken ct)
    {
        var ids = posts.Select(p => p.Id).ToList();
        var all = await db.Snapshots.AsNoTracking().Where(s => ids.Contains(s.PublishedPostId)).ToListAsync(ct);
        var latest = all.GroupBy(s => s.PublishedPostId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.TakenAt).First());
        foreach (var byAccount in posts.GroupBy(p => p.AccountId!.Value))
        {
            if (!accounts.TryGetValue(byAccount.Key, out var account)) continue;
            var points = new List<MetricPoint>();
            foreach (var day in byAccount.GroupBy(p => DateOnly.FromDateTime(p.PublishedAt.UtcDateTime)))
            {
                var snaps = day.Select(p => latest.GetValueOrDefault(p.Id)).ToList();
                if (snaps.Any(s => s is null)) continue;
                decimal? Sum(Func<PostMetricSnapshot, decimal?> f) => snaps.Any(s => f(s!) is null) ? null : snaps.Sum(s => f(s!)!.Value);
                points.Add(new MetricPoint(productId, "engagements", day.Key, day.Key, Sum(s => s.Reactions is null || s.Comments is null || s.Shares is null ? null : s.Reactions + s.Comments + s.Shares), account.Channel));
                points.Add(new MetricPoint(productId, "impressions", day.Key, day.Key, Sum(s => s.Views), account.Channel));
                points.Add(new MetricPoint(productId, "clicks", day.Key, day.Key, Sum(s => s.Clicks), account.Channel));
            }
            if (points.Count > 0) await sink!.RecordAsync($"{account.Provider}:{account.DisplayName} (posts)", "Social", productId, points, now, ct);
        }
    }
}

/// <summary>
/// Pulls each connected account's recent daily figures into analytics. An adapter that cannot supply insights is skipped entirely,
/// so a source is never shown as fresh when nothing was actually retrieved.
/// </summary>
public sealed class PullInsightsHandler(PublishingDbContext db, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IClock clock, IMetricSink? sink = null) : IJobHandler
{
    public const string PullJob = "publishing.pull-insights";
    public const int WindowDays = 7;
    public string Type => PullJob;
    public PauseScope? PauseScope => null;   // read-only

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        if (sink is null) return;
        var now = clock.UtcNow; var today = DateOnly.FromDateTime(now.UtcDateTime);
        TransientJobException? failure = null;
        foreach (var a in await db.Accounts.AsNoTracking().Where(a => a.Status == AccountStatus.Connected).ToListAsync(ct))
        {
            var publisher = publishers.FirstOrDefault(p => p.Provider == a.Provider);
            if (publisher is null) continue;
            try
            {
                var points = await publisher.GetInsightsAsync(new PostRequest(a.ExternalId, vault.Open(a.TokenCipher), ""), today.AddDays(-WindowDays), today.AddDays(-1), ct);
                if (points is null) continue;   // not supported by this adapter
                await sink.RecordAsync($"{a.Provider}:{a.DisplayName}", "Social", a.ProductId,
                    points.Select(p => new MetricPoint(a.ProductId, p.MetricKey, p.Day, p.Day, p.Value, a.Channel)).ToList(), now, ct);
            }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable or PublishErrorKind.Unknown) { failure ??= new TransientJobException($"Insights for '{a.DisplayName}': {ex.Message}", ex.RetryAfter); }
            catch (PublishException) { /* auth/permanent: the token check flags the account; freshness stays honest by not advancing */ }
            catch (System.Security.Cryptography.CryptographicException) { /* unreadable token: flagged by the token check */ }
        }
        if (failure is not null) throw failure;
    }
}

/// <summary>
/// Pulls the Page's own figures (followers, views, reach, follower changes) for each connected account and keeps them per day, with the reason when Facebook
/// supplied nothing. Followers are also recorded in analytics as a level. A figure the network refuses is stored as missing with its reason, never as zero.
/// </summary>
public sealed class PullPageFiguresHandler(PublishingDbContext db, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IClock clock, IMetricSink? sink = null) : IJobHandler
{
    public const string PullJob = "publishing.pull-page-figures";
    public const int WindowDays = 14;
    public string Type => PullJob;
    public PauseScope? PauseScope => null;   // read-only

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        var now = clock.UtcNow; var today = DateOnly.FromDateTime(now.UtcDateTime);
        TransientJobException? failure = null;
        foreach (var a in await db.Accounts.AsNoTracking().Where(a => a.Status == AccountStatus.Connected).ToListAsync(ct))
        {
            var publisher = publishers.FirstOrDefault(p => p.Provider == a.Provider);
            if (publisher is null) continue;
            try
            {
                var results = await publisher.GetPageFiguresAsync(new PostRequest(a.ExternalId, vault.Open(a.TokenCipher), ""), today.AddDays(-WindowDays), today.AddDays(-1), ct);
                if (results is null) continue;   // this adapter cannot report on the Page
                var existing = await db.PageFigures.Where(f => f.AccountId == a.Id && f.Day >= today.AddDays(-WindowDays - 1)).ToListAsync(ct);
                foreach (var r in results)
                {
                    if (r.Values.Count == 0)
                    {
                        // Nothing supplied: say so for today, so the page shows a dash with the reason and a once-good figure is not overwritten.
                        Upsert(existing, a, r.Figure, today, null, null, r.Reason, now);
                        continue;
                    }
                    foreach (var v in r.Values) Upsert(existing, a, r.Figure, v.Day, v.Value, r.MetricUsed, null, now);
                }
                await db.SaveChangesAsync(ct);
                if (sink is not null && results.FirstOrDefault(r => r.Figure == "followers") is { Values.Count: > 0 } fol)
                    await sink.RecordAsync($"{a.Provider}:{a.DisplayName} (page)", "Social", a.ProductId, fol.Values.Select(v => new MetricPoint(a.ProductId, "followers", v.Day, v.Day, v.Value, a.Channel)).ToList(), now, ct);
            }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable or PublishErrorKind.Unknown) { failure ??= new TransientJobException($"Page figures for '{a.DisplayName}': {ex.Message}", ex.RetryAfter); }
            catch (PublishException) { /* auth/permanent: the token check flags the account */ }
            catch (System.Security.Cryptography.CryptographicException) { /* unreadable token: flagged by the token check */ }
        }
        if (failure is not null) throw failure;
    }

    private void Upsert(List<PageFigure> existing, ChannelAccount a, string figure, DateOnly day, long? value, string? metric, string? reason, DateTimeOffset now)
    {
        var row = existing.FirstOrDefault(f => f.Figure == figure && f.Day == day);
        if (row is null) { row = new PageFigure { AccountId = a.Id, ProductId = a.ProductId, Figure = figure, Day = day }; db.PageFigures.Add(row); existing.Add(row); }
        else if (value is null && row.Value is not null) return;   // a refusal does not erase a figure that was reported
        row.Value = value; row.MetricUsed = metric; row.Reason = reason is null ? null : (reason.Length > 480 ? reason[..480] : reason); row.MeasuredAt = now;
    }
}

/// <summary>Account state for the autopilot's alerts. No tokens leave this module.</summary>
public sealed class ChannelHealthReader(PublishingDbContext db) : IChannelHealth
{
    public async Task<IReadOnlyList<ChannelHealth>> ListAsync(Guid productId, CancellationToken ct) =>
        (await db.Accounts.AsNoTracking().Where(a => a.ProductId == productId).OrderBy(a => a.DisplayName).ToListAsync(ct))
        .Select(a => new ChannelHealth(a.Channel, a.DisplayName, a.Status.ToString(), a.TokenExpiresAt, a.LastError)).ToList();
}
