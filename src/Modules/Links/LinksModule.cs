using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Links;

/// <summary>
/// Tracked links: a short address that counts the click and sends the visitor on to the real page with UTM tags (and the Google Play referrer for store pages).
/// What it measures is clicks. The game cannot report where an install came from, so this never says "installs".
/// </summary>
public static partial class LinksModule
{
    public static IServiceCollection AddLinksModule(this IServiceCollection s, IConfiguration c) =>
        s.AddModuleDbContext<LinksDbContext>(c, LinksDbContext.Schema).AddScoped<ITrackedLinks, TrackedLinksService>();

    public sealed record CreateLinkRequest(string? Url, string? Channel, string? Label);

    public static IEndpointRouteBuilder MapLinksEndpoints(this IEndpointRouteBuilder app)
    {
        // The public redirect. Anyone may open it; it records the click and must never fail because the record could not be written.
        app.MapGet("/r/{code}", async (string code, HttpContext http, LinksDbContext db, IConfiguration cfg, IClock clock, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!CodePattern().IsMatch(code)) return Results.NotFound();
            var link = await db.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, ct);
            if (link is null) return Results.NotFound();
            try
            {
                var ua = http.Request.Headers.UserAgent.ToString();
                db.Clicks.Add(new LinkClick { LinkId = link.Id, At = clock.UtcNow, IsBot = VisitorRules.LooksLikeRobot(ua), VisitorHash = VisitorRules.Hash(Secret(cfg), clock.UtcNow, ClientAddress(http), ua) });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logs.CreateLogger("Links").LogWarning(ex, "Could not record a click on {Code}", code); }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Robots-Tag"] = "noindex";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Redirect(UrlTracking.Build(link.Destination, link.Channel, link.Campaign, link.Code), permanent: false);
        }).AllowAnonymous().WithTags("Tracked links");

        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Tracked links");

        g.MapPost("/products/{productId:guid}/links", async (Guid productId, CreateLinkRequest req, ITrackedLinks links, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            if (!UrlTracking.IsWebAddress(req.Url)) return Results.Problem(statusCode: 400, title: "Enter a web address starting with http:// or https://");
            var channel = string.IsNullOrWhiteSpace(req.Channel) ? "other" : req.Channel.Trim();
            if (channel.Length > 60) return Results.Problem(statusCode: 400, title: "The channel name is too long.");
            var made = await links.CreateAsync(productId, req.Url!.Trim(), channel, "growth-engine", req.Label, ct);
            if (made is null) return Results.Problem(statusCode: 409, title: "No public address is configured for short links on this server.");
            await audit.WriteAsync(new AuditEntry("link.created", "TrackedLink", made.Id.ToString(), productId, made.ShortUrl, After: new { made.Destination, channel }), ct);
            return Results.Created(made.ShortUrl, new { made.Id, made.Code, made.ShortUrl, made.Destination });
        });

        g.MapGet("/products/{productId:guid}/links/report", async (Guid productId, int? days, LinksDbContext db, IConfiguration cfg, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var span = Math.Clamp(days ?? 30, 1, 365);
            var since = clock.UtcNow.AddDays(-span);
            var links = await db.Links.AsNoTracking().Where(l => l.ProductId == productId).OrderByDescending(l => l.CreatedAt).Take(500).ToListAsync(ct);
            var ids = links.Select(l => l.Id).ToList();
            var clicks = await db.Clicks.AsNoTracking().Where(c => ids.Contains(c.LinkId) && !c.IsBot && c.At >= since).Select(c => new { c.LinkId, c.At, c.VisitorHash }).ToListAsync(ct);
            var byLink = clicks.GroupBy(c => c.LinkId).ToDictionary(x => x.Key);
            var baseUrl = BaseUrl(cfg);
            object Row(LinkRow l) => new { l.Id, l.Code, shortUrl = baseUrl is null ? null : $"{baseUrl}/r/{l.Code}", l.Destination, l.Label, l.Channel, l.ContentItemId, l.CreatedAt,
                clicks = byLink.TryGetValue(l.Id, out var g1) ? g1.Count() : 0, unique = byLink.TryGetValue(l.Id, out var g2) ? g2.Select(c => c.VisitorHash).Distinct().Count() : 0,
                lastClickAt = byLink.TryGetValue(l.Id, out var g3) ? g3.Max(c => (DateTimeOffset?)c.At) : null };
            var byPost = links.Where(l => l.ContentItemId != null).GroupBy(l => l.ContentItemId!.Value).Select(grp =>
            {
                var own = grp.Select(l => l.Id).ToHashSet();
                var cs = clicks.Where(c => own.Contains(c.LinkId)).ToList();
                return new { contentItemId = grp.Key, clicks = cs.Count, unique = cs.Select(c => c.VisitorHash).Distinct().Count(), lastClickAt = cs.Max(c => (DateTimeOffset?)c.At), links = grp.Select(Row).ToList() };
            }).OrderByDescending(x => x.clicks).ToList();
            var byDay = clicks.GroupBy(c => DateOnly.FromDateTime(c.At.UtcDateTime)).OrderBy(x => x.Key).Select(x => new { day = x.Key, clicks = x.Count() }).ToList();
            return Results.Ok(new
            {
                days = span, since,
                totalClicks = clicks.Count, totalUnique = clicks.Select(c => c.VisitorHash).Distinct().Count(),
                note = "Clicks are people opening a link. Installs, views and sales are not measured here. Link previews and robots are left out.",
                byPost, byDay, loose = links.Where(l => l.ContentItemId == null).Select(Row).ToList(),
            });
        });
        return app;
    }

    internal static string? BaseUrl(IConfiguration cfg) => (cfg["Links:PublicBaseUrl"] ?? cfg["Messaging:PublicBaseUrl"])?.Trim().TrimEnd('/') is { Length: > 0 } b ? b : null;

    private static string Secret(IConfiguration cfg) => cfg["Links:HashSecret"] ?? cfg["Messaging:LinkSecret"] ?? "links";

    /// <summary>Behind the proxies the real client is the last entry of X-Forwarded-For (the proxy in front appends it, so earlier entries could be forged).</summary>
    private static string ClientAddress(HttpContext http) =>
        http.Request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? http.Connection.RemoteIpAddress?.ToString() ?? "";

    [GeneratedRegex("^[0-9A-Za-z]{4,16}$")] private static partial Regex CodePattern();
}

/// <summary>Who counts as a visitor. Link previews and crawlers fetch a link without a person behind them, so they are not clicks.</summary>
public static partial class VisitorRules
{
    [GeneratedRegex(@"bot|crawl|spider|slurp|facebookexternalhit|facebot|preview|fetch|monitor|curl/|wget|python|go-http|java/|whatsapp|telegram|discord|slack|skype|pinterest|headlesschrome|lighthouse|pingdom|uptime", RegexOptions.IgnoreCase)]
    private static partial Regex Robot();

    public static bool LooksLikeRobot(string? userAgent) => string.IsNullOrWhiteSpace(userAgent) || Robot().IsMatch(userAgent);

    /// <summary>A one-way hash that changes every day: enough to estimate distinct visitors within a day, useless for following anyone.</summary>
    public static string Hash(string secret, DateTimeOffset now, string address, string? userAgent)
    {
        var data = Encoding.UTF8.GetBytes($"{now.UtcDateTime:yyyyMMdd}|{address}|{userAgent}");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data), 0, 8).ToLowerInvariant();
    }
}

/// <summary>Adds tracking to an address. Never changes the page the visitor lands on.</summary>
public static partial class UrlTracking
{
    public static bool IsWebAddress(string? url) => !string.IsNullOrWhiteSpace(url) && url.Length <= 2000 && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(u.UserInfo);

    public static string Slug(string? v)
    {
        var s = SlugChars().Replace((v ?? "").ToLowerInvariant(), "-").Trim('-');
        return s.Length > 40 ? s[..40].Trim('-') : s;
    }

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex SlugChars();

    /// <summary>
    /// Google Play store pages get a <c>referrer</c> parameter holding the UTM tags (the form Play Console reads, and the game can read as the install referrer).
    /// Every other address gets plain utm_source / utm_medium / utm_campaign / utm_content. Tags already on the address are kept.
    /// </summary>
    public static string Build(string destination, string channel, string campaign, string code)
    {
        var hash = destination.IndexOf('#'); var fragment = hash >= 0 ? destination[hash..] : ""; var main = hash >= 0 ? destination[..hash] : destination;
        var q = main.IndexOf('?'); var query = q >= 0 ? main[(q + 1)..] : ""; var path = q >= 0 ? main[..q] : main;
        var source = Slug(channel) is { Length: > 0 } s ? s : "other"; var camp = Slug(campaign) is { Length: > 0 } c ? c : "growth-engine";
        var tags = new List<(string Key, string Value)> { ("utm_source", source), ("utm_medium", "social"), ("utm_campaign", camp), ("utm_content", code) };
        var present = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isPlay = Uri.TryCreate(destination, UriKind.Absolute, out var u) && u.Host.Equals("play.google.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith("/store/apps/details", StringComparison.OrdinalIgnoreCase);
        var added = new List<string>();
        if (isPlay)
        {
            if (!present.Contains("referrer")) added.Add("referrer=" + Uri.EscapeDataString(string.Join("&", tags.Select(t => $"{t.Key}={t.Value}"))));
        }
        else foreach (var (k, v) in tags) if (!present.Contains(k)) added.Add($"{k}={Uri.EscapeDataString(v)}");
        var all = string.Join("&", new[] { query }.Concat(added).Where(x => x.Length > 0));
        return path + (all.Length > 0 ? "?" + all : "") + fragment;
    }
}

public sealed partial class TrackedLinksService(LinksDbContext db, IConfiguration cfg, IClock clock) : ITrackedLinks
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public async Task<TrackedLink?> CreateAsync(Guid productId, string destinationUrl, string channel, string campaign, string? label, CancellationToken ct = default)
    {
        var baseUrl = LinksModule.BaseUrl(cfg);
        if (baseUrl is null) return null;
        if (!UrlTracking.IsWebAddress(destinationUrl)) throw new ArgumentException("The destination must be a web address.", nameof(destinationUrl));
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var code = NewCode(7 + attempt / 3);
            if (await db.Links.AnyAsync(x => x.Code == code, ct)) continue;
            var row = new LinkRow
            {
                ProductId = productId, Code = code, Destination = destinationUrl.Trim(), Channel = channel.Length > 60 ? channel[..60] : channel, Campaign = UrlTracking.Slug(campaign),
                Label = label is { Length: > 200 } ? label[..200] : label, CreatedAt = clock.UtcNow,
            };
            db.Links.Add(row);
            await db.SaveChangesAsync(ct);
            return new TrackedLink(row.Id, code, $"{baseUrl}/r/{code}", row.Destination);
        }
        throw new InvalidOperationException("Could not find a free short code.");
    }

    public async Task AttachAsync(IReadOnlyCollection<Guid> linkIds, Guid contentItemId, CancellationToken ct = default)
    {
        if (linkIds.Count == 0) return;
        await db.Links.Where(l => linkIds.Contains(l.Id)).ExecuteUpdateAsync(x => x.SetProperty(l => l.ContentItemId, contentItemId), ct);
    }

    public async Task<IReadOnlySet<string>> OwnShortUrlsAsync(Guid productId, IReadOnlyCollection<string> urls, CancellationToken ct = default)
    {
        var baseUrl = LinksModule.BaseUrl(cfg);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (baseUrl is null || urls.Count == 0) return result;
        var prefix = baseUrl + "/r/";
        var byCode = urls.Where(u => u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).GroupBy(u => u[prefix.Length..].TrimEnd('.', ',', ';', '!', '?', ')')).ToDictionary(g => g.Key, g => g.ToList());
        if (byCode.Count == 0) return result;
        var codes = byCode.Keys.ToList();
        foreach (var code in await db.Links.AsNoTracking().Where(l => l.ProductId == productId && codes.Contains(l.Code)).Select(l => l.Code).ToListAsync(ct))
            foreach (var u in byCode[code]) result.Add(u);
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, LinkClicks>> ClicksByContentAsync(IReadOnlyCollection<Guid> contentItemIds, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        if (contentItemIds.Count == 0) return new Dictionary<Guid, LinkClicks>();
        var links = await db.Links.AsNoTracking().Where(l => l.ContentItemId != null && contentItemIds.Contains(l.ContentItemId.Value)).Select(l => new { l.Id, Item = l.ContentItemId!.Value }).ToListAsync(ct);
        var ids = links.Select(l => l.Id).ToList();
        var q = db.Clicks.AsNoTracking().Where(c => ids.Contains(c.LinkId) && !c.IsBot);
        if (since is { } s) q = q.Where(c => c.At >= s);
        var clicks = await q.Select(c => new { c.LinkId, c.At, c.VisitorHash }).ToListAsync(ct);
        var item = links.ToDictionary(l => l.Id, l => l.Item);
        // a post with a link and no clicks is reported as zero, not left out
        var result = links.Select(l => l.Item).Distinct().ToDictionary(i => i, i => new LinkClicks(i, 0, 0, null));
        foreach (var g in clicks.GroupBy(c => item[c.LinkId])) result[g.Key] = new LinkClicks(g.Key, g.Count(), g.Select(c => c.VisitorHash).Distinct().Count(), g.Max(c => (DateTimeOffset?)c.At));
        return result;
    }

    private static string NewCode(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }
}
