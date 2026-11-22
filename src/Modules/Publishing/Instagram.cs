using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Publishing;

/// <summary>The Instagram professional account that is linked to a Facebook Page.</summary>
public sealed record InstagramAccount(string Id, string Username);

/// <summary>
/// Instagram does not accept uploaded bytes: it downloads each picture or video from a public address. This keeps a short-lived copy of the file under a signed, expiring address,
/// serves it to Instagram, and deletes it when the post is done. The address cannot be guessed, stops working after two hours, and only ever names one copied file.
/// </summary>
public sealed class PublicMedia(IObjectStorage storage, IConfiguration cfg, IClock clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    public sealed record Stored(string Url, string Key);

    public string? BaseUrl => (cfg["Publishing:PublicMedia:BaseUrl"] ?? cfg["Messaging:PublicBaseUrl"])?.Trim().TrimEnd('/') is { Length: > 0 } b ? b : null;
    private string Secret => cfg["Publishing:PublicMedia:Secret"] ?? cfg["Messaging:LinkSecret"] ?? throw new InvalidOperationException("Messaging:LinkSecret is required to sign public media addresses");

    public async Task<Stored> StoreAsync(Stream content, string extension, CancellationToken ct)
    {
        var baseUrl = BaseUrl ?? throw new PublishException(PublishErrorKind.Permanent, "No public address is configured for this server, so Instagram cannot download the media (set Messaging:PublicBaseUrl).");
        var name = $"{Guid.NewGuid():N}.{extension}";
        var key = "public-tmp/" + name;
        await storage.PutAsync(key, content, ContentTypeFor(extension), ct);
        var expires = clock.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        return new Stored($"{baseUrl}/m/{name}?e={expires}&s={Sign(name, expires)}", key);
    }

    public Task DeleteAsync(string key) => storage.DeleteAsync(key, CancellationToken.None);

    public static string ContentTypeFor(string extension) => extension switch { "jpg" => "image/jpeg", "mp4" => "video/mp4", _ => "application/octet-stream" };

    public string Sign(string name, long expires) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{name}|{expires}"))).ToLowerInvariant();

    /// <summary>Opens the copy if the signature matches and the address has not expired. Returns null otherwise.</summary>
    public async Task<(Stream Stream, string ContentType)?> OpenAsync(string name, long expires, string signature, CancellationToken ct)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || name.Length > 64 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.'))) return null;
        var ext = name[(dot + 1)..];
        if (ext is not ("jpg" or "mp4")) return null;
        if (expires < clock.UtcNow.ToUnixTimeSeconds()) return null;
        var expected = Encoding.UTF8.GetBytes(Sign(name, expires)); var given = Encoding.UTF8.GetBytes(signature ?? "");
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return null;
        var stream = await storage.OpenReadAsync("public-tmp/" + name, ct);
        return stream is null ? null : (stream, ContentTypeFor(ext));
    }

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/m/{name}", async (string name, long e, string s, PublicMedia media, HttpContext http, CancellationToken ct) =>
        {
            var opened = await media.OpenAsync(name, e, s, ct);
            if (opened is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(opened.Value.Stream, opened.Value.ContentType, enableRangeProcessing: true);
        }).AllowAnonymous().WithTags("Public media");
}

/// <summary>Makes a picture acceptable to Instagram: JPEG only, at most 1440 pixels on a side, and a shape between 4:5 and 1.91:1.</summary>
public static class InstagramImages
{
    public const double MinRatio = 0.8, MaxRatio = 1.91;
    public const int MaxSide = 1440;

    public static async Task<(MemoryStream Jpeg, int Width, int Height)> PrepareAsync(Stream input, string name, CancellationToken ct)
    {
        Image image;
        try { image = await Image.LoadAsync(input, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new PublishException(PublishErrorKind.Permanent, $"'{name}' could not be read as a picture.", inner: ex); }
        using (image)
        {
            var ratio = (double)image.Width / image.Height;
            if (ratio < MinRatio || ratio > MaxRatio)
                throw new PublishException(PublishErrorKind.Permanent, $"'{name}' is {image.Width}x{image.Height}. Instagram accepts pictures between 4:5 (tall) and 1.91:1 (wide); crop it first.");
            image.Mutate(c =>
            {
                c.BackgroundColor(Color.White);   // transparent areas would otherwise turn black in a JPEG
                if (Math.Max(image.Width, image.Height) > MaxSide) c.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(MaxSide, MaxSide) });
            });
            var output = new MemoryStream();
            await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 88 }, ct);
            output.Position = 0;
            return (output, image.Width, image.Height);
        }
    }
}

/// <summary>
/// Publishes to the Instagram professional account that is linked to a Facebook Page, through the same Graph API and the same Page token. A single picture, a set of pictures (carousel),
/// a Reel or a Story. Instagram has no text-only posts, so a post without media is refused before anything is sent.
/// </summary>
public sealed class InstagramGraph(IHttpClientFactory http, IConfiguration cfg, PublicMedia media) : IChannelPublisher
{
    private string Version => cfg["Publishing:Facebook:GraphVersion"] ?? "v21.0";
    private string Base => $"https://graph.facebook.com/{Version}";
    private TimeSpan Poll => TimeSpan.FromSeconds(Math.Max(0.01, cfg.GetValue("Publishing:Instagram:PollSeconds", 5.0)));
    private TimeSpan MaxWait => TimeSpan.FromSeconds(Math.Max(0.05, cfg.GetValue("Publishing:Instagram:MaxWaitSeconds", 240.0)));

    public string Provider => "instagram";
    public int MaxTextLength => 2200;
    public IReadOnlyCollection<string> SupportedFormats { get; } = ["Post", "Story", "Reel"];

    public async Task<PostResult> PublishAsync(PostRequest r, CancellationToken ct)
    {
        var files = r.Media ?? [];
        if (files.Count == 0) throw new PublishException(PublishErrorKind.Permanent, "Instagram needs a picture or a video: it has no text-only posts.");
        var hashtags = r.Text.Split(' ', '\n').Count(w => w.StartsWith('#') && w.Length > 1);
        if (hashtags > 30) throw new PublishException(PublishErrorKind.Permanent, "Instagram allows at most 30 hashtags in one post.");
        var temp = new List<string>();
        try
        {
            string container;
            switch (r.Format)
            {
                case "Post" when files.Any(f => f.IsVideo): throw new PublishException(PublishErrorKind.Permanent, "Instagram feed posts are pictures. Post a video as a Reel.");
                case "Post" when files.Count == 1:
                    container = await CreateAsync(r, new() { ["image_url"] = await StoreImageAsync(files[0], temp, ct), ["caption"] = r.Text }, ct);
                    break;
                case "Post":
                    if (files.Count > 10) throw new PublishException(PublishErrorKind.Permanent, "A carousel holds at most 10 pictures.");
                    var children = new List<string>();
                    foreach (var f in files)
                    {
                        var child = await CreateAsync(r, new() { ["image_url"] = await StoreImageAsync(f, temp, ct), ["is_carousel_item"] = "true" }, ct);
                        await WaitUntilReadyAsync(child, r.AccessToken, ct);
                        children.Add(child);
                    }
                    container = await CreateAsync(r, new() { ["media_type"] = "CAROUSEL", ["children"] = string.Join(",", children), ["caption"] = r.Text }, ct);
                    break;
                case "Reel":
                    if (files.Count != 1 || !files[0].IsVideo) throw new PublishException(PublishErrorKind.Permanent, "A Reel is exactly one video.");
                    container = await CreateAsync(r, new() { ["media_type"] = "REELS", ["video_url"] = await StoreVideoAsync(files[0], temp, ct), ["caption"] = r.Text, ["share_to_feed"] = "true" }, ct);
                    break;
                case "Story":
                    if (files.Count != 1) throw new PublishException(PublishErrorKind.Permanent, "A Story is exactly one picture or one video.");
                    container = await CreateAsync(r, files[0].IsVideo
                        ? new() { ["media_type"] = "STORIES", ["video_url"] = await StoreVideoAsync(files[0], temp, ct) }
                        : new() { ["media_type"] = "STORIES", ["image_url"] = await StoreImageAsync(files[0], temp, ct) }, ct);
                    break;
                default: throw new PublishException(PublishErrorKind.Permanent, $"Instagram cannot publish '{r.Format}'.");
            }
            await WaitUntilReadyAsync(container, r.AccessToken, ct);
            var published = await FacebookGraph.Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{r.ExternalId}/media_publish",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["creation_id"] = container, ["access_token"] = r.AccessToken }), ct), ct);
            var id = published.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) throw new PublishException(PublishErrorKind.Unknown, "Instagram did not return the id of the post; it may or may not have gone out.");
            string? url = null;
            try { url = (await GetAsync($"{Base}/{id}?fields=permalink&access_token={Uri.EscapeDataString(r.AccessToken)}", ct)).TryGetProperty("permalink", out var p) ? p.GetString() : null; }
            catch (PublishException) { /* the post is live; a missing link is not a failure */ }
            return new PostResult(id, url);
        }
        finally { foreach (var key in temp) { try { await media.DeleteAsync(key); } catch (Exception) { /* a leftover copy expires by itself and is harmless */ } } }
    }

    private async Task<string> StoreImageAsync(MediaFile f, List<string> temp, CancellationToken ct)
    {
        await using var src = await f.Open(ct);
        var (jpeg, _, _) = await InstagramImages.PrepareAsync(src, f.FileName, ct);
        await using (jpeg) { var stored = await media.StoreAsync(jpeg, "jpg", ct); temp.Add(stored.Key); return stored.Url; }
    }

    private async Task<string> StoreVideoAsync(MediaFile f, List<string> temp, CancellationToken ct)
    {
        if (!f.ContentType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase)) throw new PublishException(PublishErrorKind.Permanent, $"'{f.FileName}' is not an MP4 video, which Instagram requires.");
        await using var src = await f.Open(ct);
        var stored = await media.StoreAsync(src, "mp4", ct); temp.Add(stored.Key); return stored.Url;
    }

    private async Task<string> CreateAsync(PostRequest r, Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["access_token"] = r.AccessToken;
        var res = await FacebookGraph.Send(() => http.CreateClient("facebook").PostAsync($"{Base}/{r.ExternalId}/media", new FormUrlEncodedContent(fields), ct), ct);
        return res.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s ? s : throw new PublishException(PublishErrorKind.Unknown, "Instagram did not accept the media.");
    }

    /// <summary>Instagram prepares each upload before it can be published. Waits for it, and says plainly if it was refused.</summary>
    private async Task WaitUntilReadyAsync(string container, string token, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + MaxWait;
        while (true)
        {
            var res = await GetAsync($"{Base}/{container}?fields=status_code,status&access_token={Uri.EscapeDataString(token)}", ct);
            var code = res.TryGetProperty("status_code", out var c) ? c.GetString() : null;
            if (code == "FINISHED") return;
            if (code is "ERROR" or "EXPIRED") throw new PublishException(PublishErrorKind.Permanent, "Instagram could not process the media: " + (res.TryGetProperty("status", out var st) ? st.GetString() : code));
            if (DateTimeOffset.UtcNow > deadline) throw new PublishException(PublishErrorKind.Retryable, "Instagram is still processing the media.", TimeSpan.FromMinutes(2));
            await Task.Delay(Poll, ct);
        }
    }

    public async Task<PostResult?> FindRecentAsync(PostRequest r, CancellationToken ct)
    {
        var res = await GetAsync($"{Base}/{r.ExternalId}/media?fields=id,caption,permalink,timestamp&limit=10&access_token={Uri.EscapeDataString(r.AccessToken)}", ct);
        if (!res.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var m in data.EnumerateArray())
        {
            if (!m.TryGetProperty("caption", out var cap) || (cap.GetString() ?? "").Trim() != r.Text.Trim()) continue;
            if (!m.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), out var at) || DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(15)) continue;
            return new PostResult(m.GetProperty("id").GetString()!, m.TryGetProperty("permalink", out var pl) ? pl.GetString() : null);
        }
        return null;
    }

    public async Task<bool> IsAccountValidAsync(string externalId, string accessToken, CancellationToken ct)
    {
        try { await GetAsync($"{Base}/{externalId}?fields=id,username&access_token={Uri.EscapeDataString(accessToken)}", ct); return true; }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.AuthExpired or PublishErrorKind.Permanent) { return false; }
    }

    /// <summary>Likes and comments from the media record; reach, shares and views from insights when Instagram gives them. Anything it does not give stays null.</summary>
    public async Task<PostMetrics?> GetPostMetricsAsync(PostRequest account, string providerPostId, CancellationToken ct)
    {
        var token = Uri.EscapeDataString(account.AccessToken);
        int? likes = null, comments = null, shares = null; long? views = null, reach = null; var notes = new List<string>();
        try
        {
            var m = await GetAsync($"{Base}/{providerPostId}?fields=like_count,comments_count&access_token={token}", ct);
            likes = m.TryGetProperty("like_count", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
            comments = m.TryGetProperty("comments_count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
        }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent) { notes.Add("likes and comments: " + Short(ex.Message)); }
        foreach (var metric in new[] { "reach", "shares", "views" })
        {
            try
            {
                var res = await GetAsync($"{Base}/{providerPostId}/insights?metric={metric}&access_token={token}", ct);
                var value = res.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 && d[0].TryGetProperty("values", out var vs) && vs.GetArrayLength() > 0 && vs[0].TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : (long?)null;
                if (metric == "reach") reach = value; else if (metric == "shares") shares = value is null ? null : (int)value; else views = value;
            }
            catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent) { notes.Add($"{metric}: {Short(ex.Message)}"); }
        }
        return new PostMetrics(likes, comments, shares, views, reach, null, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    public async Task<IReadOnlyList<PageFigureResult>?> GetPageFiguresAsync(PostRequest account, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var token = Uri.EscapeDataString(account.AccessToken);
        var results = new List<PageFigureResult>();
        try
        {
            var res = await GetAsync($"{Base}/{account.ExternalId}?fields=followers_count&access_token={token}", ct);
            results.Add(res.TryGetProperty("followers_count", out var f) && f.ValueKind == JsonValueKind.Number
                ? new PageFigureResult("followers", "followers_count", null, [new PageFigureValue(DateOnly.FromDateTime(DateTime.UtcNow), f.GetInt64())])
                : new PageFigureResult("followers", null, "Instagram did not return followers_count for this account", []));
        }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent or PublishErrorKind.AuthExpired) { results.Add(new PageFigureResult("followers", null, Short(ex.Message), [])); }

        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var until = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        try
        {
            var res = await GetAsync($"{Base}/{account.ExternalId}/insights?metric=reach&period=day&since={since}&until={until}&access_token={token}", ct);
            var values = new List<PageFigureValue>();
            if (res.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 && d[0].TryGetProperty("values", out var vs))
                foreach (var v in vs.EnumerateArray())
                    if (v.TryGetProperty("value", out var n) && n.ValueKind == JsonValueKind.Number && v.TryGetProperty("end_time", out var et) && DateTimeOffset.TryParse(et.GetString(), out var end))
                        values.Add(new PageFigureValue(DateOnly.FromDateTime(end.UtcDateTime.AddDays(-1)), n.GetInt64()));   // end_time is the end of the day measured
            results.Add(values.Count > 0 ? new PageFigureResult("page_reach", "reach", null, values) : new PageFigureResult("page_reach", null, "Instagram returned no daily reach", []));
        }
        catch (PublishException ex) when (ex.Kind is PublishErrorKind.Permanent or PublishErrorKind.AuthExpired) { results.Add(new PageFigureResult("page_reach", null, Short(ex.Message), [])); }
        return results;
    }

    private Task<JsonElement> GetAsync(string url, CancellationToken ct) => FacebookGraph.Send(() => http.CreateClient("facebook").GetAsync(url, ct), ct);
    private static string Short(string s) => s.Length > 220 ? s[..220] : s;
}
