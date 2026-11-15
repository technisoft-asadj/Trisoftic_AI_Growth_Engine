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
public sealed class PublishPipeline(PublishingDbContext db, IContentPublishSource content, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IClock clock, IAuditWriter audit)
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
        if (string.IsNullOrWhiteSpace(item.Body)) { await FailAsync(item, "The approved text is empty."); return; }
        if (item.Body.Length > publisher.MaxTextLength) { await FailAsync(item, $"The text is {item.Body.Length} characters; {item.Channel} allows {publisher.MaxTextLength}."); return; }

        string token;
        try { token = vault.Open(account.TokenCipher); }
        catch (System.Security.Cryptography.CryptographicException) { await FailAsync(item, "The stored account permission can no longer be read. Reconnect the account."); return; }

        var claim = await content.BeginPublishAsync(itemId, version, ct);
        if (!claim.Ok) return;   // lost a race with an edit/unschedule: leave it alone

        var req = new PostRequest(account.ExternalId, token, item.Body);
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
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { /* next minute */ }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
