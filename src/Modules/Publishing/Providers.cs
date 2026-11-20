using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace TriSoftic.GrowthEngine.Modules.Publishing;

/// <summary>
/// How a provider failure should be handled.
/// Retryable: the request clearly did not take effect (connection refused, rate limit) so sending again is safe.
/// Unknown: the request may have been accepted (timeout, 5xx) so check the provider before sending again, or a duplicate post is possible.
/// Permanent: the provider refused this content or request; retrying cannot help.
/// AuthExpired: the account's permission is gone; a person must reconnect it.
/// </summary>
public enum PublishErrorKind { Retryable, Unknown, Permanent, AuthExpired }

public sealed class PublishException(PublishErrorKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
{
    public PublishErrorKind Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>One file to send with a post. The bytes are opened only when needed and disposed by the caller of <see cref="Open"/>, so a large video is streamed rather than held in memory.</summary>
public sealed record MediaFile(string FileName, string ContentType, long Size, Func<CancellationToken, Task<Stream>> Open)
{
    public bool IsVideo => ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}

public sealed record PostRequest(string ExternalId, string AccessToken, string Text, IReadOnlyList<MediaFile>? Media = null, string Format = "Post");
public sealed record PostResult(string ProviderPostId, string? Url);
public sealed record PageInfo(string Id, string Name, string AccessToken);

/// <summary>One adapter per social network. The pipeline knows nothing about any provider beyond this.</summary>
public interface IChannelPublisher
{
    string Provider { get; }
    int MaxTextLength { get; }
    /// <summary>Content formats this adapter can send. Everything else is refused up front rather than posted wrongly.</summary>
    IReadOnlyCollection<string> SupportedFormats { get; }

    /// <summary>Returns only after the provider confirmed the post and gave its id.</summary>
    Task<PostResult> PublishAsync(PostRequest request, CancellationToken ct);

    /// <summary>Looks for a post with this exact text made in the last few minutes. Used before retrying after an uncertain failure.</summary>
    Task<PostResult?> FindRecentAsync(PostRequest request, CancellationToken ct);

    Task<bool> IsAccountValidAsync(string externalId, string accessToken, CancellationToken ct);

    /// <summary>
    /// Daily figures from the network (impressions, reach, followers...) using the platform's metric keys. Returns null when this adapter cannot supply them,
    /// in which case nothing is recorded and no freshness is claimed.
    /// </summary>
    Task<IReadOnlyList<InsightPoint>?> GetInsightsAsync(PostRequest account, DateOnly from, DateOnly to, CancellationToken ct) => Task.FromResult<IReadOnlyList<InsightPoint>?>(null);

    /// <summary>
    /// The network's own figures for one published post. A figure the network did not supply is null, never zero.
    /// Returns null when this adapter cannot report on posts at all.
    /// </summary>
    Task<PostMetrics?> GetPostMetricsAsync(PostRequest account, string providerPostId, CancellationToken ct) => Task.FromResult<PostMetrics?>(null);

    /// <summary>
    /// The Page's own figures: followers now, and per-day views, reach and follower changes. One result per figure, each saying which network metric
    /// answered or why none did. Returns null when this adapter cannot report on the Page at all.
    /// </summary>
    Task<IReadOnlyList<PageFigureResult>?> GetPageFiguresAsync(PostRequest account, DateOnly from, DateOnly to, CancellationToken ct) => Task.FromResult<IReadOnlyList<PageFigureResult>?>(null);

    /// <summary>The newest top-level comments on one published post. Null when this adapter cannot read comments.</summary>
    Task<IReadOnlyList<ProviderComment>?> GetCommentsAsync(PostRequest account, string providerPostId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderComment>?>(null);

    /// <summary>Posts one reply under a comment and returns the reply's id. Null when this adapter cannot reply. Only ever called after a person approved the text.</summary>
    Task<string?> ReplyToCommentAsync(PostRequest account, string commentId, string text, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Hides or unhides a comment (it stays visible to its author and their friends). False when this adapter cannot. Only ever called after a person confirmed.</summary>
    Task<bool> SetCommentHiddenAsync(PostRequest account, string commentId, bool hidden, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>One public comment as the network reports it. <see cref="AuthorId"/> lets the Page's own comments be skipped.</summary>
public sealed record ProviderComment(string Id, string? AuthorId, string? AuthorName, string Text, DateTimeOffset CreatedAt, bool IsHidden);

public sealed record PageFigureValue(DateOnly Day, long Value);

/// <summary>One Page figure. <see cref="Values"/> empty means the network supplied nothing, and <see cref="Reason"/> says why; it is never reported as zero.</summary>
public sealed record PageFigureResult(string Figure, string? MetricUsed, string? Reason, IReadOnlyList<PageFigureValue> Values);

/// <summary>Figures for one post. Every field is null when the network could not provide it; <see cref="Note"/> says why.</summary>
public sealed record PostMetrics(int? Reactions, int? Comments, int? Shares, long? Views, long? Reach, int? Clicks, string? Note);

/// <summary>A pretend network for demos and tests: accepts anything, remembers what it was given. Enabled only when Publishing:AllowSandbox is true.</summary>
public sealed class SandboxPublisher : IChannelPublisher
{
    private static readonly ConcurrentDictionary<string, (string Text, DateTimeOffset At, string Id)> Posts = new();

    public string Provider => "sandbox";
    public int MaxTextLength => 5000;
    public IReadOnlyCollection<string> SupportedFormats { get; } = ["Post", "Story", "Reel"];

    public Task<PostResult> PublishAsync(PostRequest r, CancellationToken ct)
    {
        var id = "sandbox-" + Guid.NewGuid().ToString("N")[..12];
        Posts[id] = (r.Text, DateTimeOffset.UtcNow, id);
        return Task.FromResult(new PostResult(id, $"https://sandbox.invalid/{r.ExternalId}/{id}"));
    }

    public Task<PostResult?> FindRecentAsync(PostRequest r, CancellationToken ct)
    {
        var hit = Posts.Values.FirstOrDefault(p => p.Text == r.Text && DateTimeOffset.UtcNow - p.At < TimeSpan.FromMinutes(15));
        return Task.FromResult<PostResult?>(hit.Id is null ? null : new PostResult(hit.Id, $"https://sandbox.invalid/{r.ExternalId}/{hit.Id}"));
    }

    public Task<bool> IsAccountValidAsync(string externalId, string accessToken, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>The part of Facebook's Graph API the platform uses, separated so the OAuth flow can be tested without Facebook.</summary>
public interface IFacebookGraph
{
    bool IsConfigured { get; }
    string AuthorizeUrl(string state);
    Task<string> ExchangeCodeAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<PageInfo>> ListPagesAsync(string userToken, CancellationToken ct);
}

public sealed class FacebookGraph(IHttpClientFactory http, IConfiguration cfg) : IFacebookGraph, IChannelPublisher
{
    private string Version => cfg["Publishing:Facebook:GraphVersion"] ?? "v21.0";
    private string AppId => cfg["Publishing:Facebook:AppId"] ?? "";
    private string AppSecret => cfg["Publishing:Facebook:AppSecret"] ?? "";
    private string RedirectUri => cfg["Publishing:Facebook:RedirectUri"] ?? "";
    private string Base => $"https://graph.facebook.com/{Version}";
    public bool IsConfigured => AppId != "" && AppSecret != "" && RedirectUri != "";

    // ---- IChannelPublisher ----
    public string Provider => "facebook-page";
    public int MaxTextLength => 60000;
    public IReadOnlyCollection<string> SupportedFormats { get; } = ["Post", "Story", "Reel"];   // text, photos, one video, photo and video stories, reels

    // ---- OAuth ----
    public string AuthorizeUrl(string state)
        => $"https://www.facebook.com/{Version}/dialog/oauth?client_id={Uri.EscapeDataString(AppId)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state={Uri.EscapeDataString(state)}&scope=pages_show_list,pages_manage_posts,pages_read_engagement,pages_read_user_content,pages_manage_engagement,read_insights";

    public async Task<string> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var short1 = await GetJson($"{Base}/oauth/access_token?client_id={AppId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&client_secret={AppSecret}&code={Uri.EscapeDataString(code)}", ct);
        var shortToken = short1.GetProperty("access_token").GetString()!;
        // Page tokens obtained from a long-lived user token do not expire, so extend before listing pages.
        var long1 = await GetJson($"{Base}/oauth/access_token?grant_type=fb_exchange_token&client_id={AppId}&client_secret={AppSecret}&fb_exchange_token={Uri.EscapeDataString(shortToken)}", ct);
        return long1.GetProperty("access_token").GetString()!;
    }

    public async Task<IReadOnlyList<PageInfo>> ListPagesAsync(string userToken, CancellationToken ct)
    {
        var res = await GetJson($"{Base}/me/accounts?fields=id,name,access_token&limit=100&access_token={Uri.EscapeDataString(userToken)}", ct);
        return res.GetProperty("data").EnumerateArray().Select(p => new PageInfo(p.GetProperty("id").GetString()!, p.GetProperty("name").GetString() ?? "", p.GetProperty("access_token").GetString()!)).ToList();
    }

    // ---- publishing ----
    // Text goes to /feed. One photo goes to /photos (published, with the text as its caption). Several photos are uploaded unpublished and then attached to one /feed post.
    // A video goes to /videos (the text is its description). A story uploads one unpublished photo and then turns it into a Page photo story.
    public async Task<PostResult> PublishAsync(PostRequest r, CancellationToken ct)
    {
        var media = r.Media ?? [];
        var page = r.ExternalId;
        string id;
        if (r.Format == "Reel")
        {
            if (media.Count != 1 || !media[0].IsVideo) throw new PublishException(PublishErrorKind.Permanent, "A reel needs exactly one video.");
            id = (await UploadVideoThroughSession(page, "video_reels", r.AccessToken, media[0], r.Text, publishNow: true, ct)).Id;
        }
        else if (r.Format == "Story" && media.Count == 1 && media[0].IsVideo)
        {
            var done = await UploadVideoThroughSession(page, "video_stories", r.AccessToken, media[0], null, publishNow: false, ct);
            id = done.PostId ?? done.Id;
        }
        else if (r.Format == "Story")
        {
            if (media.Count != 1) throw new PublishException(PublishErrorKind.Permanent, "A story needs exactly one image or one video.");
            var photoId = (await UploadPhoto(page, r.AccessToken, media[0], null, published: false, ct)).Id;
            var res = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{page}/photo_stories", new FormUrlEncodedContent(new Dictionary<string, string> { ["photo_id"] = photoId, ["access_token"] = r.AccessToken }), ct), ct);
            id = res.TryGetProperty("post_id", out var pid) ? pid.GetString() ?? photoId : photoId;
        }
        else if (media.Count == 0)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = r.Text, ["access_token"] = r.AccessToken });
            var res = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{page}/feed", form, ct), ct);
            id = res.GetProperty("id").GetString() ?? throw new PublishException(PublishErrorKind.Unknown, "Facebook accepted the request but returned no post id");
        }
        else if (media.Count == 1 && media[0].IsVideo)
        {
            var res = await PostMultipart($"{Base}/{page}/videos", new() { ["description"] = r.Text, ["access_token"] = r.AccessToken }, media[0], "source", ct);
            id = res.GetProperty("id").GetString() ?? throw new PublishException(PublishErrorKind.Unknown, "Facebook accepted the video but returned no id");
        }
        else if (media.Count == 1)
        {
            var photo = await UploadPhoto(page, r.AccessToken, media[0], r.Text, published: true, ct);
            id = photo.PostId ?? photo.Id;
        }
        else
        {
            var fields = new Dictionary<string, string> { ["message"] = r.Text, ["access_token"] = r.AccessToken };
            for (var i = 0; i < media.Count; i++) fields[$"attached_media[{i}]"] = JsonSerializer.Serialize(new { media_fbid = (await UploadPhoto(page, r.AccessToken, media[i], null, published: false, ct)).Id });
            var res = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{page}/feed", new FormUrlEncodedContent(fields), ct), ct);
            id = res.GetProperty("id").GetString() ?? throw new PublishException(PublishErrorKind.Unknown, "Facebook accepted the request but returned no post id");
        }

        string? url = null;
        try { url = (await GetJson($"{Base}/{id}?fields=permalink_url&access_token={Uri.EscapeDataString(r.AccessToken)}", ct)).GetProperty("permalink_url").GetString(); }
        catch (PublishException) { /* the post exists; a missing link is not a failure */ }
        return new PostResult(id, url is null ? null : (url.StartsWith("http") ? url : "https://www.facebook.com" + url));
    }

    /// <summary>
    /// Reels and video stories use the same three steps: ask Facebook for an upload session, send the file to the upload address it returns (streamed, never held in memory),
    /// then finish. Facebook processes the video afterwards, so the status is checked for a short while to catch an immediate rejection.
    /// There is no idempotency key on this flow: a failure after "finish" is therefore checked against the Page's videos before any retry (see <see cref="FindRecentAsync"/>).
    /// </summary>
    private async Task<Uploaded> UploadVideoThroughSession(string page, string edge, string token, MediaFile video, string? description, bool publishNow, CancellationToken ct)
    {
        var start = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{page}/{edge}", new FormUrlEncodedContent(new Dictionary<string, string> { ["upload_phase"] = "start", ["access_token"] = token }), ct), ct);
        var videoId = start.TryGetProperty("video_id", out var vid) ? vid.GetString() : null;
        if (string.IsNullOrEmpty(videoId)) throw new PublishException(PublishErrorKind.Unknown, "Facebook did not open an upload session");
        var uploadUrl = start.TryGetProperty("upload_url", out var up) && !string.IsNullOrEmpty(up.GetString()) ? up.GetString()! : $"https://rupload.facebook.com/video-upload/{Version}/{videoId}";

        await Send(async () =>
        {
            await using var stream = await video.Open(ct);
            using var content = new StreamContent(stream); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token); req.Headers.TryAddWithoutValidation("offset", "0"); req.Headers.TryAddWithoutValidation("file_size", video.Size.ToString());
            return await http.CreateClient("facebook-upload").SendAsync(req, ct);
        }, ct);

        var fields = new Dictionary<string, string> { ["upload_phase"] = "finish", ["video_id"] = videoId, ["access_token"] = token };
        if (publishNow) { fields["video_state"] = "PUBLISHED"; if (!string.IsNullOrWhiteSpace(description)) fields["description"] = description; }
        var fin = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{page}/{edge}", new FormUrlEncodedContent(fields), ct), ct);
        if (fin.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) throw new PublishException(PublishErrorKind.Permanent, "Facebook did not accept the finished upload");

        for (var i = 0; i < 6; i++)   // up to ~30 s: enough to see an immediate processing failure; a long video may legitimately still be processing
        {
            try
            {
                var st = await GetJson($"{Base}/{videoId}?fields=status&access_token={Uri.EscapeDataString(token)}", ct);
                if (st.TryGetProperty("status", out var s) && s.TryGetProperty("video_status", out var vs))
                {
                    var state = vs.GetString();
                    if (state == "error") throw new PublishException(PublishErrorKind.Permanent, "Facebook could not process the video: " + (s.TryGetProperty("processing_phase", out var pp) && pp.TryGetProperty("error", out var pe) && pe.TryGetProperty("message", out var pm) ? pm.GetString() : "no reason given"));
                    if (state == "ready") break;
                }
            }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable or PublishErrorKind.Unknown) { /* a hiccup while polling must not undo an upload that succeeded */ }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return new Uploaded(videoId, fin.TryGetProperty("post_id", out var pid) ? pid.GetString() : null);
    }

    private sealed record Uploaded(string Id, string? PostId);

    private async Task<Uploaded> UploadPhoto(string page, string token, MediaFile file, string? caption, bool published, CancellationToken ct)
    {
        var fields = new Dictionary<string, string> { ["published"] = published ? "true" : "false", ["access_token"] = token };
        if (!string.IsNullOrWhiteSpace(caption)) fields["caption"] = caption;
        var res = await PostMultipart($"{Base}/{page}/photos", fields, file, "source", ct);
        var id = res.GetProperty("id").GetString() ?? throw new PublishException(PublishErrorKind.Unknown, "Facebook accepted the photo but returned no id");
        return new Uploaded(id, res.TryGetProperty("post_id", out var p) ? p.GetString() : null);
    }

    /// <summary>Sends one file with the given form fields. The file is streamed from storage and the request uses the long-timeout client (videos are slow).</summary>
    private Task<JsonElement> PostMultipart(string url, Dictionary<string, string> fields, MediaFile file, string fileField, CancellationToken ct)
        => Send(async () =>
        {
            await using var stream = await file.Open(ct);
            using var form = new MultipartFormDataContent();
            foreach (var kv in fields) form.Add(new StringContent(kv.Value), kv.Key);
            var part = new StreamContent(stream); part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            form.Add(part, fileField, file.FileName);
            return await http.CreateClient("facebook-upload").PostAsync(url, form, ct);
        }, ct);

    public async Task<PostResult?> FindRecentAsync(PostRequest r, CancellationToken ct)
    {
        // A story has no text and Facebook offers no reliable way to look one up, so after an uncertain failure it is not retried blindly: a person checks the Page.
        if (r.Format == "Story") throw new PublishException(PublishErrorKind.Permanent, "The story may or may not have been posted and cannot be checked automatically. Look at the Page's story: if it is there use 'mark as published', otherwise retry.");
        if (r.Format == "Reel")
        {
            // Reels are listed with the Page's videos, with the reel's text as the description.
            var vids = await GetJson($"{Base}/{r.ExternalId}/videos?fields=id,description,created_time,permalink_url&limit=10&access_token={Uri.EscapeDataString(r.AccessToken)}", ct);
            foreach (var v in vids.GetProperty("data").EnumerateArray())
                if (v.TryGetProperty("description", out var d) && d.GetString() == r.Text
                    && v.TryGetProperty("created_time", out var vt) && DateTimeOffset.TryParse(vt.GetString(), out var vat) && DateTimeOffset.UtcNow - vat < TimeSpan.FromMinutes(30))
                    return new PostResult(v.GetProperty("id").GetString()!, v.TryGetProperty("permalink_url", out var vu) ? vu.GetString() : null);
            return null;
        }
        var res = await GetJson($"{Base}/{r.ExternalId}/posts?fields=id,message,created_time,permalink_url&limit=10&access_token={Uri.EscapeDataString(r.AccessToken)}", ct);
        foreach (var p in res.GetProperty("data").EnumerateArray())
        {
            if (p.TryGetProperty("message", out var m) && m.GetString() == r.Text
                && p.TryGetProperty("created_time", out var t) && DateTimeOffset.TryParse(t.GetString(), out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(30))
                return new PostResult(p.GetProperty("id").GetString()!, p.TryGetProperty("permalink_url", out var u) ? u.GetString() : null);
        }
        return null;
    }

    // Every figure is asked for on its own, so one that Facebook refuses leaves only that figure empty (with the reason noted) and never blanks the rest.
    //   reactions, comments:  need pages_read_user_content (or Page Public Content Access)
    //   shares:               pages_read_engagement
    //   views, reach, clicks: read_insights. Meta renames its metrics from time to time, so each has fallback names.
    private static readonly string[] ViewMetrics = ["post_media_view", "post_impressions"];
    private static readonly string[] ReachMetrics = ["post_total_media_view_unique", "post_impressions_unique"];
    private static readonly string[] ClickMetrics = ["post_clicks"];

    public async Task<PostMetrics?> GetPostMetricsAsync(PostRequest account, string providerPostId, CancellationToken ct)
    {
        var token = Uri.EscapeDataString(account.AccessToken);
        var missing = new List<string>(); string? firstReason = null;
        void Missing(string label, PublishException ex) { missing.Add(label); firstReason ??= ex.Message.Length > 220 ? ex.Message[..220] : ex.Message; }

        async Task<int?> Count(string label, string field)
        {
            try
            {
                var res = await GetJson($"{Base}/{providerPostId}?fields={field}.summary(total_count).limit(0)&access_token={token}", ct);
                return res.TryGetProperty(field, out var o) && o.TryGetProperty("summary", out var s) && s.TryGetProperty("total_count", out var t) ? t.GetInt32() : null;
            }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent or PublishErrorKind.AuthExpired) { Missing(label, ex); return null; }
        }
        var reactions = await Count("reactions", "reactions"); var comments = await Count("comments", "comments");

        int? shares = null;
        try
        {
            var res = await GetJson($"{Base}/{providerPostId}?fields=shares&access_token={token}", ct);
            shares = res.TryGetProperty("shares", out var sh) && sh.TryGetProperty("count", out var c) ? c.GetInt32() : 0;   // Facebook leaves "shares" out when there are none
        }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent or PublishErrorKind.AuthExpired) { Missing("shares", ex); }

        async Task<long?> Insight(string label, string[] candidates)
        {
            PublishException? last = null;
            foreach (var metric in candidates)
            {
                try
                {
                    var res = await GetJson($"{Base}/{providerPostId}/insights?metric={metric}&access_token={token}", ct);
                    foreach (var row in res.GetProperty("data").EnumerateArray())
                        if (row.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0 && vals[0].TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt64();
                }
                catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent or PublishErrorKind.AuthExpired) { last = ex; }   // refused or retired metric: try the next name
            }
            Missing(label, last ?? new PublishException(PublishErrorKind.Permanent, "no value returned yet (the post may be too new)"));
            return null;
        }
        var views = await Insight("views", ViewMetrics); var reach = await Insight("reach", ReachMetrics); var clicks = await Insight("clicks", ClickMetrics);

        var note = missing.Count == 0 ? null : $"Facebook did not supply {string.Join(", ", missing)}. First reason: {firstReason}";
        return new PostMetrics(reactions, comments, shares, views, reach, (int?)clicks, note);
    }

    // Page figures. Meta renames and retires Page insight metrics (impressions became "views" in 2025, more changes were announced), so each figure has an
    // ordered list of metric names, overridable in configuration (Publishing:Facebook:PageMetrics:page_views = "name1,name2") without a release.
    private static readonly (string Figure, string[] Metrics)[] PageFigureDefs =
    [
        ("page_views", ["page_media_view", "page_impressions"]),
        ("page_reach", ["page_total_media_view_unique", "page_impressions_unique"]),
        ("new_follows", ["page_daily_follows_unique", "page_daily_follows"]),
        ("lost_follows", ["page_daily_unfollows_unique"]),
    ];

    public async Task<IReadOnlyList<PageFigureResult>?> GetPageFiguresAsync(PostRequest account, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var token = Uri.EscapeDataString(account.AccessToken);
        var results = new List<PageFigureResult>();
        static string Short(string s) => s.Length > 220 ? s[..220] : s;

        // Followers: the Page's current count (a level, recorded for today). Likes ("fan_count") are a different number and are never substituted.
        try
        {
            var res = await GetJson($"{Base}/{account.ExternalId}?fields=followers_count&access_token={token}", ct);
            results.Add(res.TryGetProperty("followers_count", out var f) && f.ValueKind == JsonValueKind.Number
                ? new PageFigureResult("followers", "followers_count", null, [new PageFigureValue(DateOnly.FromDateTime(DateTime.UtcNow), f.GetInt64())])
                : new PageFigureResult("followers", null, "Facebook did not return followers_count for this Page", []));
        }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent or PublishErrorKind.AuthExpired) { results.Add(new PageFigureResult("followers", null, Short(ex.Message), [])); }

        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var until = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        foreach (var (figure, defaults) in PageFigureDefs)
        {
            var names = cfg[$"Publishing:Facebook:PageMetrics:{figure}"] is { Length: > 0 } o ? o.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : defaults;
            string? reason = null; PageFigureResult? found = null;
            foreach (var metric in names)
            {
                try
                {
                    var res = await GetJson($"{Base}/{account.ExternalId}/insights?metric={metric}&period=day&since={since}&until={until}&access_token={token}", ct);
                    var values = new List<PageFigureValue>();
                    foreach (var row in res.GetProperty("data").EnumerateArray())
                        if (row.TryGetProperty("values", out var vals))
                            foreach (var v in vals.EnumerateArray())
                                if (v.TryGetProperty("value", out var n) && n.ValueKind == JsonValueKind.Number && v.TryGetProperty("end_time", out var et) && DateTimeOffset.TryParse(et.GetString(), out var end))
                                    values.Add(new PageFigureValue(DateOnly.FromDateTime(end.UtcDateTime).AddDays(-1), n.GetInt64()));   // end_time closes the day, so the day itself is the one before
                    if (values.Count > 0) { found = new PageFigureResult(figure, metric, null, values); break; }
                    reason = $"{metric}: Facebook returned no values for this period";
                }
                catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent or PublishErrorKind.AuthExpired) { reason = Short($"{metric}: {ex.Message}"); }   // refused or retired: try the next name
            }
            results.Add(found ?? new PageFigureResult(figure, null, reason ?? "no metric configured", []));
        }
        return results;
    }

    // Comments need pages_read_user_content (reading) and pages_manage_engagement (replying, hiding).
    public async Task<IReadOnlyList<ProviderComment>?> GetCommentsAsync(PostRequest account, string providerPostId, CancellationToken ct)
    {
        var fields = Uri.EscapeDataString("id,message,from{id,name},created_time,is_hidden");
        var res = await GetJson($"{Base}/{providerPostId}/comments?filter=toplevel&order=reverse_chronological&limit=50&fields={fields}&access_token={Uri.EscapeDataString(account.AccessToken)}", ct);
        var list = new List<ProviderComment>();
        if (!res.TryGetProperty("data", out var data)) return list;
        foreach (var c in data.EnumerateArray())
        {
            var id = c.TryGetProperty("id", out var i) ? i.GetString() : null;
            var text = c.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(text)) continue;   // photo-only or empty comments have nothing to answer
            string? authorId = null, authorName = null;
            if (c.TryGetProperty("from", out var f)) { authorId = f.TryGetProperty("id", out var fi) ? fi.GetString() : null; authorName = f.TryGetProperty("name", out var fn) ? fn.GetString() : null; }
            var at = c.TryGetProperty("created_time", out var ct2) && DateTimeOffset.TryParse(ct2.GetString(), out var parsed) ? parsed : DateTimeOffset.UtcNow;
            list.Add(new ProviderComment(id, authorId, authorName, text, at, c.TryGetProperty("is_hidden", out var h) && h.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    public async Task<string?> ReplyToCommentAsync(PostRequest account, string commentId, string text, CancellationToken ct)
    {
        var res = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{commentId}/comments", new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = text, ["access_token"] = account.AccessToken }), ct), ct);
        return res.TryGetProperty("id", out var id) ? id.GetString() : throw new PublishException(PublishErrorKind.Unknown, "Facebook did not return the new reply's id");
    }

    public async Task<bool> SetCommentHiddenAsync(PostRequest account, string commentId, bool hidden, CancellationToken ct)
    {
        var res = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{commentId}", new FormUrlEncodedContent(new Dictionary<string, string> { ["is_hidden"] = hidden ? "true" : "false", ["access_token"] = account.AccessToken }), ct), ct);
        return res.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
    }

    public async Task<bool> IsAccountValidAsync(string externalId, string accessToken, CancellationToken ct)
    {
        try
        {
            var res = await GetJson($"{Base}/debug_token?input_token={Uri.EscapeDataString(accessToken)}&access_token={AppId}|{AppSecret}", ct);
            return res.GetProperty("data").GetProperty("is_valid").GetBoolean();
        }
        catch (PublishException ex) when (ex.Kind == PublishErrorKind.AuthExpired) { return false; }
    }

    private Task<JsonElement> GetJson(string url, CancellationToken ct) => Send(() => http.CreateClient("facebook").GetAsync(url, ct), ct);

    /// <summary>Calls Graph and maps every failure to a <see cref="PublishErrorKind"/>.</summary>
    private static async Task<JsonElement> Send(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await call(); }
        catch (HttpRequestException ex) when (ex.StatusCode is null) { throw new PublishException(PublishErrorKind.Retryable, "Could not reach Facebook", inner: ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new PublishException(PublishErrorKind.Unknown, "Facebook did not answer in time; the post may or may not have gone out", inner: ex); }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonElement root = default;
            try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); } catch (JsonException) { /* handled below */ }
            if (res.IsSuccessStatusCode && root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("error", out _)) return root;

            var code = 0; var message = $"Facebook answered {(int)res.StatusCode}"; var transient = false;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            {
                code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                message = err.TryGetProperty("message", out var m) ? m.GetString() ?? message : message;
                transient = err.TryGetProperty("is_transient", out var t) && t.ValueKind == JsonValueKind.True;
            }
            message = message.Length > 500 ? message[..500] : message;
            if (code == 190 || res.StatusCode == HttpStatusCode.Unauthorized) throw new PublishException(PublishErrorKind.AuthExpired, "Facebook no longer accepts this account's permission: " + message);
            if (code is 4 or 17 or 32 or 613 || res.StatusCode == HttpStatusCode.TooManyRequests) throw new PublishException(PublishErrorKind.Retryable, "Facebook rate limit: " + message, TimeSpan.FromMinutes(10));
            if (code is 1 or 2 || transient || (int)res.StatusCode >= 500) throw new PublishException(PublishErrorKind.Unknown, "Facebook had a problem handling the request: " + message);
            throw new PublishException(PublishErrorKind.Permanent, message);
        }
    }
}

/// <summary>One day's figure from a network's own reporting. A null value means the network could not provide it.</summary>
public sealed record InsightPoint(string MetricKey, DateOnly Day, decimal? Value);
