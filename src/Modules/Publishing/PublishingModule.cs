using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Publishing;

public static class PublishingModule
{
    public sealed record SandboxIn(string Channel, string? Name);
    public sealed record SelectIn(string PageId);
    public sealed record VersionIn(int Version);
    public sealed record ManualIn(int Version, string Url, string? ProviderPostId, string? Note);

    private sealed record StatePayload(Guid ProductId, Guid UserId, string Channel, DateTimeOffset Expires, string? Kind = null);

    /// <param name="runScheduler">Hosts that run background jobs set this so due posts are dispatched. Several hosts doing so is harmless.</param>
    public static IServiceCollection AddPublishingModule(this IServiceCollection s, IConfiguration c, bool runScheduler)
    {
        s.AddModuleDbContext<PublishingDbContext>(c, PublishingDbContext.Schema);
        // The key ring must be shared by the API and the worker (same application name, and a shared folder in production) or the worker cannot read stored tokens.
        var dp = s.AddDataProtection().SetApplicationName("TriSoftic.GrowthEngine");
        if (c["DataProtection:KeysPath"] is { Length: > 0 } keys) dp.PersistKeysToFileSystem(new DirectoryInfo(keys));
        s.AddHttpClient("facebook", h => h.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();   // request URLs can carry access tokens, so they are not logged
        s.AddHttpClient("facebook-upload", h => h.Timeout = TimeSpan.FromMinutes(15)).RemoveAllLoggers();   // photos and videos are slow to upload
        s.AddScoped<TokenVault>();
        s.AddScoped<FacebookGraph>();
        s.AddScoped<IFacebookGraph>(sp => sp.GetRequiredService<FacebookGraph>());
        s.AddScoped<IChannelPublisher>(sp => sp.GetRequiredService<FacebookGraph>());
        if (c.GetValue<bool>("Publishing:AllowSandbox")) s.AddScoped<IChannelPublisher, SandboxPublisher>();
        s.AddScoped<PublicMedia>();
        s.AddScoped<IChannelPublisher, InstagramGraph>();
        s.AddScoped<PublishPipeline>();
        s.AddScoped<IChannelHealth, ChannelHealthReader>();
        s.AddScoped<IJobHandler, PublishJobHandler>();
        s.AddScoped<IJobHandler, DispatchHandler>();
        s.AddScoped<IJobHandler, TokenCheckHandler>();
        s.AddScoped<IJobHandler, PullInsightsHandler>();
        s.AddScoped<IJobHandler, PullPostMetricsHandler>();
        s.AddScoped<IJobHandler, PullPageFiguresHandler>();
        s.AddScoped<IJobHandler, PullCommentsHandler>();
        s.AddScoped<IPostPerformanceReader, PostPerformanceReader>();
        if (runScheduler || c.GetValue<bool>("Jobs:RunInProcess")) s.AddHostedService<PublishingScheduler>();
        return s;
    }

    private static object AccountDto(ChannelAccount a) => new { a.Id, a.ProductId, a.Provider, a.Channel, a.ExternalId, a.DisplayName, a.Status, a.LastError, a.LastCheckedAt, a.ConnectedAt };   // never the token

    public static IEndpointRouteBuilder MapPublishingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/publishing").RequireAuthorization().WithTags("Publishing");
        MapAccounts(g);
        MapFailures(g);
        MapPageHealth(g);
        CommentEndpoints.Map(g);
        MapCallback(app);
        PublicMedia.Map(app);
        return app;
    }

    private static void MapAccounts(RouteGroupBuilder g)
    {
        g.MapGet("/capabilities", (IConfiguration cfg, IFacebookGraph fb, IEnumerable<IChannelPublisher> pubs) => Results.Ok(new
        {
            facebookConfigured = fb.IsConfigured, sandboxEnabled = pubs.Any(p => p.Provider == "sandbox"),
            providers = pubs.Select(p => new { p.Provider, formats = p.SupportedFormats, maxLength = p.MaxTextLength }),
        }));

        g.MapGet("/products/{productId:guid}/accounts", async (Guid productId, PublishingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            return Results.Ok((await db.Accounts.AsNoTracking().Where(a => a.ProductId == productId && a.Status != AccountStatus.Disconnected).OrderBy(a => a.Channel).ToListAsync(ct)).Select(AccountDto));
        });

        g.MapPost("/products/{productId:guid}/accounts/sandbox", async (Guid productId, SandboxIn req, PublishingDbContext db, ICurrentUser u, IEnumerable<IChannelPublisher> pubs, TokenVault vault, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AccountsConnect) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            if (!pubs.Any(p => p.Provider == "sandbox")) return Results.Problem(statusCode: 409, title: "The sandbox channel is switched off (Publishing:AllowSandbox)");
            if (string.IsNullOrWhiteSpace(req.Channel) || req.Channel.Length > 60) return Results.Problem(statusCode: 400, title: "A channel name is required");
            var a = await ReplaceAsync(db, productId, "sandbox", req.Channel.Trim(), "sandbox-" + Guid.NewGuid().ToString("N")[..8], string.IsNullOrWhiteSpace(req.Name) ? "Sandbox page" : req.Name.Trim(), vault.Seal("sandbox"), u.UserId, clock.UtcNow, ct);
            await audit.WriteAsync(new AuditEntry("publishing.account.connected", "ChannelAccount", a.Id.ToString(), productId, a.DisplayName, After: new { a.Provider, a.Channel }), ct);
            return Results.Created($"/api/v1/publishing/accounts/{a.Id}", AccountDto(a));
        });

        g.MapGet("/connect/facebook/start", (Guid productId, string channel, string? kind, ICurrentUser u, IFacebookGraph fb, IDataProtectionProvider dp, IClock clock) =>
        {
            if (!u.HasPermission(Permissions.AccountsConnect) || u.UserId is not { } uid) return Results.Forbid();
            if (!fb.IsConfigured) return Results.Problem(statusCode: 409, title: "Facebook is not configured (Publishing:Facebook:AppId, AppSecret, RedirectUri)");
            if (string.IsNullOrWhiteSpace(channel) || channel.Length > 60) return Results.Problem(statusCode: 400, title: "A channel name is required");
            var state = dp.CreateProtector("publishing.fb-state").Protect(JsonSerializer.Serialize(new StatePayload(productId, uid, channel.Trim(), clock.UtcNow.AddMinutes(15), kind == "instagram" ? "instagram" : null)));
            return Results.Ok(new { url = fb.AuthorizeUrl(state, kind == "instagram") });
        });

        g.MapGet("/connect/pending/{id:guid}", async (Guid id, PublishingDbContext db, ICurrentUser u, TokenVault vault, IClock clock, CancellationToken ct) =>
        {
            var p = await db.Pending.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId && x.ExpiresAt > clock.UtcNow, ct);
            if (p is null) return Results.NotFound();
            var pages = JsonSerializer.Deserialize<List<PageInfo>>(vault.Open(p.PagesJsonCipher)) ?? [];
            return Results.Ok(pages.Select(x => new { x.Id, x.Name }));   // tokens stay server-side
        });

        g.MapPost("/connect/pending/{id:guid}/select", async (Guid id, SelectIn req, PublishingDbContext db, ICurrentUser u, IFacebookGraph fb, TokenVault vault, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var p = await db.Pending.FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId && x.ExpiresAt > clock.UtcNow, ct);
            if (p is null || !u.HasPermission(Permissions.AccountsConnect) || !await u.CanAccessProductAsync(p.ProductId, ct)) return Results.NotFound();
            var pages = JsonSerializer.Deserialize<List<PageInfo>>(vault.Open(p.PagesJsonCipher)) ?? [];
            var page = pages.FirstOrDefault(x => x.Id == req.PageId);
            if (page is null) return Results.Problem(statusCode: 400, title: "That page is not one of the pages offered");
            if (p.Provider.StartsWith("instagram|"))
            {
                // the Instagram professional account is found through the Page it is linked to; the Page's own token is used for it
                InstagramAccount? ig;
                try { ig = await fb.GetInstagramAccountAsync(page.Id, page.AccessToken, ct); }
                catch (PublishException ex) { return Results.Problem(statusCode: 502, title: "Facebook could not look up the Instagram account: " + ex.Message); }
                if (ig is null)
                {
                    // say what Facebook actually granted, so the cause is visible instead of guessed
                    var granted = await fb.GrantedScopesAsync(page.AccessToken, ct);
                    var missing = new[] { "instagram_basic", "instagram_content_publish" }.Where(x => !granted.Contains(x)).ToList();
                    var why = granted.Count == 0 ? "" : missing.Count > 0
                        ? $" Facebook did not give this login the permission {string.Join(" and ", missing)}: connect again, and when Facebook asks which Instagram accounts to allow, select the account and leave every permission switched on."
                        : " The Instagram permissions were granted, so the Page itself has no linked Instagram professional account: check Page settings, Linked accounts.";
                    await audit.WriteAsync(new AuditEntry("publishing.account.connect-failed", "ChannelAccount", null, p.ProductId, $"Instagram: none found for Page {page.Name}", After: new { granted }), ct);
                    return Results.Problem(statusCode: 409, title: $"No Instagram professional (Business or Creator) account was found for the Page \"{page.Name}\".{why}");
                }
                var igAccount = await ReplaceAsync(db, p.ProductId, "instagram", "Instagram", ig.Id, "@" + ig.Username, vault.Seal(page.AccessToken), u.UserId, clock.UtcNow, ct);
                db.Pending.Remove(p);
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(new AuditEntry("publishing.account.connected", "ChannelAccount", igAccount.Id.ToString(), p.ProductId, igAccount.DisplayName, After: new { igAccount.Provider, igAccount.Channel }), ct);
                return Results.Ok(AccountDto(igAccount));
            }
            var channel = p.Provider.Contains('|') ? p.Provider[(p.Provider.IndexOf('|') + 1)..] : "Facebook";   // set when the connection was started
            var a = await ReplaceAsync(db, p.ProductId, "facebook-page", channel, page.Id, page.Name, vault.Seal(page.AccessToken), u.UserId, clock.UtcNow, ct);
            db.Pending.Remove(p);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("publishing.account.connected", "ChannelAccount", a.Id.ToString(), p.ProductId, a.DisplayName, After: new { a.Provider, a.Channel }), ct);
            return Results.Ok(AccountDto(a));
        });

        g.MapPost("/accounts/{id:guid}/disconnect", async (Guid id, PublishingDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var a = await db.Accounts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null || !u.HasPermission(Permissions.AccountsConnect) || !await u.CanAccessProductAsync(a.ProductId, ct)) return Results.NotFound();
            a.Status = AccountStatus.Disconnected; a.TokenCipher = "";   // the stored permission is destroyed, not just hidden
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("publishing.account.disconnected", "ChannelAccount", id.ToString(), a.ProductId, a.DisplayName), ct);
            return Results.NoContent();
        });

        g.MapGet("/products/{productId:guid}/posts", async (Guid productId, PublishingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            return Results.Ok(await db.Posts.AsNoTracking().Where(p => p.ProductId == productId).OrderByDescending(p => p.PublishedAt).Take(100)
                .Select(p => new { p.ContentItemId, p.Version, p.ProviderPostId, p.Url, p.Source, p.Note, p.PublishedAt }).ToListAsync(ct));
        });

        // The latest reported figures for each published post. A figure the network did not supply is null ("—" in the UI), never zero.
        g.MapGet("/products/{productId:guid}/post-metrics", async (Guid productId, PublishingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var posts = await db.Posts.AsNoTracking().Where(p => p.ProductId == productId).OrderByDescending(p => p.PublishedAt).Take(100).ToListAsync(ct);
            var ids = posts.Select(p => p.Id).ToList();
            var snaps = await db.Snapshots.AsNoTracking().Where(s => ids.Contains(s.PublishedPostId)).ToListAsync(ct);
            var latest = snaps.GroupBy(s => s.PublishedPostId).ToDictionary(x => x.Key, x => x.OrderByDescending(s => s.TakenAt).First());
            return Results.Ok(posts.Select(p =>
            {
                latest.TryGetValue(p.Id, out var s);
                return new { p.ContentItemId, p.Version, p.Url, p.PublishedAt, measuredAt = s?.TakenAt, s?.Reactions, s?.Comments, s?.Shares, s?.Views, s?.Reach, s?.Clicks, s?.Note };
            }));
        });

        g.MapPost("/products/{productId:guid}/post-metrics/refresh", async (Guid productId, ICurrentUser u, IJobQueue jobs, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            await jobs.EnqueueAsync(new JobRequest(PullPostMetricsHandler.PullJob, "{}", IdempotencyKey: $"postmetrics-now:{clock.UtcNow.ToUnixTimeSeconds() / 60}"), ct);
            return Results.Accepted();
        });
    }

    private static void MapPageHealth(RouteGroupBuilder g)
    {
        // The Page's own figures: latest value, the last 28 days, and the follower change since the oldest reading in the last 7 days. A figure Facebook did not supply is null with the reason.
        g.MapGet("/products/{productId:guid}/page-health", async (Guid productId, PublishingDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            var rows = await db.PageFigures.AsNoTracking().Where(f => f.ProductId == productId && f.Day >= today.AddDays(-28)).ToListAsync(ct);
            var figures = new[] { "followers", "new_follows", "lost_follows", "page_views", "page_reach" }.Select(name =>
            {
                var mine = rows.Where(r => r.Figure == name).OrderBy(r => r.Day).ToList();
                var withValue = mine.Where(r => r.Value is not null).ToList();
                var last = withValue.LastOrDefault();
                var lastMiss = mine.Where(r => r.Value is null).OrderByDescending(r => r.MeasuredAt).FirstOrDefault();
                // follower change: latest level minus the oldest level in the last 7 days; only when there are two different days
                var weekAgo = today.AddDays(-7);
                var baseline = name == "followers" ? withValue.FirstOrDefault(r => r.Day >= weekAgo) : null;
                long? change = name == "followers" && last is not null && baseline is not null && baseline.Day < last.Day ? last.Value - baseline.Value : null;
                return new
                {
                    figure = name, value = last?.Value, day = last?.Day, metric = last?.MetricUsed, measuredAt = last?.MeasuredAt,
                    reason = last is null ? lastMiss?.Reason : null, changeSince = change is null ? (DateOnly?)null : baseline!.Day, change,
                    series = withValue.Select(r => new { day = r.Day, value = r.Value }).ToList(),
                };
            }).ToList();
            return Results.Ok(new { figures });
        });

        g.MapPost("/products/{productId:guid}/page-health/refresh", async (Guid productId, ICurrentUser u, IJobQueue jobs, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.PublishingManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            await jobs.EnqueueAsync(new JobRequest(PullPageFiguresHandler.PullJob, "{}", IdempotencyKey: $"pagefigures-now:{clock.UtcNow.ToUnixTimeSeconds() / 60}"), ct);
            return Results.Accepted();
        });
    }

    private static async Task<ChannelAccount> ReplaceAsync(PublishingDbContext db, Guid productId, string provider, string channel, string externalId, string name, string cipher, Guid? by, DateTimeOffset now, CancellationToken ct)
    {
        // One live account per product and channel: connecting a new one retires the old, so routing is never ambiguous.
        foreach (var old in await db.Accounts.Where(a => a.ProductId == productId && a.Status != AccountStatus.Disconnected && a.Channel.ToLower() == channel.ToLower()).ToListAsync(ct))
        { old.Status = AccountStatus.Disconnected; old.TokenCipher = ""; }
        var existing = await db.Accounts.FirstOrDefaultAsync(a => a.ProductId == productId && a.Provider == provider && a.ExternalId == externalId, ct);
        var acct = existing ?? new ChannelAccount { ProductId = productId, Provider = provider, ExternalId = externalId };
        acct.Channel = channel; acct.DisplayName = name; acct.TokenCipher = cipher; acct.Status = AccountStatus.Connected; acct.LastError = null; acct.ConnectedBy = by; acct.ConnectedAt = now;
        if (existing is null) db.Accounts.Add(acct);
        await db.SaveChangesAsync(ct);
        return acct;
    }

    // Facebook sends the browser back here. The session cookie is SameSite=Strict, so it is not sent on this cross-site navigation:
    // the route is anonymous and trusts only the signed, expiring state that was minted for a signed-in user.
    private static void MapCallback(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/publishing/connect/facebook/callback", async (string? code, string? state, string? error, PublishingDbContext db, IFacebookGraph fb, IDataProtectionProvider dp, TokenVault vault, IClock clock, IConfiguration cfg, CancellationToken ct) =>
        {
            var web = (cfg["Publishing:WebBaseUrl"] ?? "").TrimEnd('/');
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return Results.Redirect($"{web}/publishing/connect?error=denied");
            StatePayload? s;
            try { s = JsonSerializer.Deserialize<StatePayload>(dp.CreateProtector("publishing.fb-state").Unprotect(state)); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException) { s = null; }
            if (s is null || s.Expires < clock.UtcNow) return Results.Redirect($"{web}/publishing/connect?error=expired");
            try
            {
                var userToken = await fb.ExchangeCodeAsync(code, ct);
                var pages = await fb.ListPagesAsync(userToken, ct);
                if (pages.Count == 0) return Results.Redirect($"{web}/publishing/connect?error=nopages");
                var pending = new PendingConnection { ProductId = s.ProductId, UserId = s.UserId, Provider = (s.Kind == "instagram" ? "instagram|" : "facebook-page|") + s.Channel, PagesJsonCipher = vault.Seal(JsonSerializer.Serialize(pages)), ExpiresAt = clock.UtcNow.AddMinutes(15) };
                db.Pending.Add(pending);
                await db.SaveChangesAsync(ct);
                return Results.Redirect($"{web}/publishing/connect?pending={pending.Id}");
            }
            catch (PublishException) { return Results.Redirect($"{web}/publishing/connect?error=facebook"); }
        }).AllowAnonymous();
    }

    private static void MapFailures(RouteGroupBuilder g)
    {
        g.MapGet("/failures", async (IContentPublishSource content, PublishingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.PublishingManage)) return Results.Forbid();
            var failed = await content.GetFailedAsync(await u.AccessibleProductIdsAsync(ct), ct);
            var ids = failed.Select(f => f.ItemId).ToList();
            var attempts = await db.Attempts.AsNoTracking().Where(a => ids.Contains(a.ContentItemId)).OrderByDescending(a => a.StartedAt).ToListAsync(ct);
            return Results.Ok(failed.Select(f => new
            {
                f.ItemId, f.ProductId, f.Channel, f.Title, f.Version, reason = f.Error, f.UpdatedAt,
                attempts = attempts.Where(a => a.ContentItemId == f.ItemId && a.Version == f.Version).Select(a => new { a.Number, a.Status, a.Error, a.StartedAt }).Take(10),
            }));
        });

        // Retry: back to Scheduled for now. If an earlier attempt's outcome is unknown, the pipeline checks the provider first.
        g.MapPost("/content/{id:guid}/retry", async (Guid id, VersionIn req, IContentPublishSource content, IJobQueue jobs, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var item = await Authorize(id, req.Version, content, u, ct);
            if (item is null) return Results.NotFound();
            var r = await content.RequeueAsync(id, req.Version, clock.UtcNow, ct);
            if (!r.Ok) return Results.Problem(statusCode: 409, title: r.Error);
            await audit.WriteAsync(new AuditEntry("publishing.retry-requested", "ContentItem", id.ToString(), item.ProductId, item.Title, After: new { req.Version }), ct);
            await jobs.EnqueueAsync(new JobRequest(DispatchHandler.DispatchJob, "{}", IdempotencyKey: "dispatch-now:" + Guid.NewGuid()), ct);
            return Results.Accepted();
        });

        g.MapPost("/content/{id:guid}/abandon", async (Guid id, VersionIn req, IContentPublishSource content, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var item = await Authorize(id, req.Version, content, u, ct);
            if (item is null) return Results.NotFound();
            var r = await content.AbandonAsync(id, req.Version, ct);
            if (!r.Ok) return Results.Problem(statusCode: 409, title: r.Error);
            await audit.WriteAsync(new AuditEntry("publishing.abandoned", "ContentItem", id.ToString(), item.ProductId, item.Title, After: new { req.Version }), ct);
            return Results.NoContent();
        });

        // For when the post is on the network but we never heard so (or someone posted it by hand). Needs the link, so the claim can be checked.
        g.MapPost("/content/{id:guid}/mark-published", async (Guid id, ManualIn req, IContentPublishSource content, PublishingDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var item = await Authorize(id, req.Version, content, u, ct);
            if (item is null) return Results.NotFound();
            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Results.Problem(statusCode: 400, title: "Paste the link to the live post (http or https)");
            if (item.Status != "PublishFailed") return Results.Problem(statusCode: 409, title: $"Only failed content can be marked as published manually (was {item.Status})");
            var now = clock.UtcNow;
            if (!await db.Posts.AnyAsync(p => p.ContentItemId == id && p.Version == req.Version, ct))
                db.Posts.Add(new PublishedPost { ContentItemId = id, Version = req.Version, ProductId = item.ProductId, ProviderPostId = string.IsNullOrWhiteSpace(req.ProviderPostId) ? "manual" : req.ProviderPostId.Trim(), Url = req.Url.Trim(), Source = PostSource.Manual, Note = req.Note, RecordedBy = u.UserId, PublishedAt = now });
            await db.SaveChangesAsync(ct);
            var r = await content.CompletePublishAsync(id, req.Version, now, ct);
            if (!r.Ok) return Results.Problem(statusCode: 409, title: r.Error);
            await audit.WriteAsync(new AuditEntry("publishing.marked-published-manually", "ContentItem", id.ToString(), item.ProductId, item.Title, After: new { req.Version, req.Url, req.Note }), ct);
            return Results.NoContent();
        });
    }

    private static async Task<PublishableContent?> Authorize(Guid id, int version, IContentPublishSource content, ICurrentUser u, CancellationToken ct)
    {
        if (!u.HasPermission(Permissions.PublishingManage)) return null;
        var item = await content.GetAsync(id, version, ct);
        return item is not null && await u.CanAccessProductAsync(item.ProductId, ct) ? item : null;
    }
}
