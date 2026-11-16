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

public sealed record PostRequest(string ExternalId, string AccessToken, string Text);
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
}

/// <summary>Figures for one post. Every field is null when the network could not provide it; <see cref="Note"/> says why.</summary>
public sealed record PostMetrics(int? Reactions, int? Comments, int? Shares, long? Views, long? Reach, int? Clicks, string? Note);

/// <summary>A pretend network for demos and tests: accepts anything, remembers what it was given. Enabled only when Publishing:AllowSandbox is true.</summary>
public sealed class SandboxPublisher : IChannelPublisher
{
    private static readonly ConcurrentDictionary<string, (string Text, DateTimeOffset At, string Id)> Posts = new();

    public string Provider => "sandbox";
    public int MaxTextLength => 5000;
    public IReadOnlyCollection<string> SupportedFormats { get; } = ["Post"];

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
    public IReadOnlyCollection<string> SupportedFormats { get; } = ["Post"];   // text posts only; images/video need media upload, which is not built yet

    // ---- OAuth ----
    public string AuthorizeUrl(string state)
        => $"https://www.facebook.com/{Version}/dialog/oauth?client_id={Uri.EscapeDataString(AppId)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state={Uri.EscapeDataString(state)}&scope=pages_show_list,pages_manage_posts,pages_read_engagement,read_insights";

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
    public async Task<PostResult> PublishAsync(PostRequest r, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = r.Text, ["access_token"] = r.AccessToken });
        var res = await Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{r.ExternalId}/feed", form, ct), ct);
        var id = res.GetProperty("id").GetString() ?? throw new PublishException(PublishErrorKind.Unknown, "Facebook accepted the request but returned no post id");
        string? url = null;
        try { url = (await GetJson($"{Base}/{id}?fields=permalink_url&access_token={Uri.EscapeDataString(r.AccessToken)}", ct)).GetProperty("permalink_url").GetString(); }
        catch (PublishException) { /* the post exists; a missing link is not a failure */ }
        return new PostResult(id, url is null ? null : (url.StartsWith("http") ? url : "https://www.facebook.com" + url));
    }

    public async Task<PostResult?> FindRecentAsync(PostRequest r, CancellationToken ct)
    {
        var res = await GetJson($"{Base}/{r.ExternalId}/posts?fields=id,message,created_time,permalink_url&limit=10&access_token={Uri.EscapeDataString(r.AccessToken)}", ct);
        foreach (var p in res.GetProperty("data").EnumerateArray())
        {
            if (p.TryGetProperty("message", out var m) && m.GetString() == r.Text
                && p.TryGetProperty("created_time", out var t) && DateTimeOffset.TryParse(t.GetString(), out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(30))
                return new PostResult(p.GetProperty("id").GetString()!, p.TryGetProperty("permalink_url", out var u) ? u.GetString() : null);
        }
        return null;
    }

    // Engagement counts need only pages_read_engagement. Views, reach and clicks need read_insights, and Meta renames its metrics from time to time,
    // so each is asked for separately with fallbacks: one that is refused or retired leaves that figure empty instead of failing the rest.
    private static readonly string[] ViewMetrics = ["post_media_view", "post_impressions"];
    private static readonly string[] ReachMetrics = ["post_total_media_view_unique", "post_impressions_unique"];
    private static readonly string[] ClickMetrics = ["post_clicks"];

    public async Task<PostMetrics?> GetPostMetricsAsync(PostRequest account, string providerPostId, CancellationToken ct)
    {
        var token = Uri.EscapeDataString(account.AccessToken);
        var basic = await GetJson($"{Base}/{providerPostId}?fields=reactions.summary(total_count).limit(0),comments.summary(total_count).limit(0),shares&access_token={token}", ct);
        static int? Total(JsonElement e, string name) => e.TryGetProperty(name, out var o) && o.TryGetProperty("summary", out var s) && s.TryGetProperty("total_count", out var t) ? t.GetInt32() : null;
        var reactions = Total(basic, "reactions"); var comments = Total(basic, "comments");
        int? shares = reactions is null && comments is null ? null : (basic.TryGetProperty("shares", out var sh) && sh.TryGetProperty("count", out var c) ? c.GetInt32() : 0);   // Facebook leaves "shares" out when there are none

        var missing = new List<string>();
        async Task<long?> Insight(string label, string[] candidates)
        {
            foreach (var metric in candidates)
            {
                try
                {
                    var res = await GetJson($"{Base}/{providerPostId}/insights?metric={metric}&access_token={token}", ct);
                    foreach (var row in res.GetProperty("data").EnumerateArray())
                        if (row.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0 && vals[0].TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt64();
                }
                catch (PublishException ex) when (ex.Kind == PublishErrorKind.Permanent) { /* refused or retired metric: try the next name */ }
            }
            missing.Add(label);
            return null;
        }
        var views = await Insight("views", ViewMetrics); var reach = await Insight("reach", ReachMetrics); var clicks = await Insight("clicks", ClickMetrics);
        var note = missing.Count == 0 ? null : $"Facebook did not supply {string.Join(", ", missing)} (the connection may lack the read_insights permission, or the post is too new).";
        return new PostMetrics(reactions, comments, shares, views, reach, (int?)clicks, note);
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
