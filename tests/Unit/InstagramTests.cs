using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Publishing;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class InstagramTests
{
    // ---- test doubles ----
    private sealed class MemStorage : IObjectStorage
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default) { using var ms = new MemoryStream(); await content.CopyToAsync(ms, ct); Files[key] = ms.ToArray(); }
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream?>(Files.TryGetValue(key, out var b) ? new MemoryStream(b) : null);
        public Task DeleteAsync(string key, CancellationToken ct = default) { Files.Remove(key); return Task.CompletedTask; }
    }

    private sealed class Clock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }

    private sealed record Call(HttpMethod Method, string Path, string Query, Dictionary<string, string> Form);

    private sealed class Graph(MemStorage storage) : HttpMessageHandler
    {
        public readonly List<Call> Calls = [];
        public readonly List<string> StoredAtCall = [];
        public int StatusChecksBeforeFinished = 0;
        public string StatusCode = "FINISHED";
        public Func<Call, (HttpStatusCode, string)?>? Override;
        private int _containers; private int _checks;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var form = new Dictionary<string, string>();
            if (req.Content is not null) foreach (var pair in (await req.Content.ReadAsStringAsync(ct)).Split('&', StringSplitOptions.RemoveEmptyEntries)) { var kv = pair.Split('=', 2); form[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv.Length > 1 ? kv[1].Replace('+', ' ') : ""); }
            var call = new Call(req.Method, req.RequestUri!.AbsolutePath, req.RequestUri.Query, form);
            Calls.Add(call); StoredAtCall.Add(string.Join(",", storage.Files.Keys));
            var (status, body) = Override?.Invoke(call) ?? Default(call);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private (HttpStatusCode, string) Default(Call c)
        {
            if (c.Method == HttpMethod.Post && c.Path.EndsWith("/media")) return (HttpStatusCode.OK, $"{{\"id\":\"c{++_containers}\"}}");
            if (c.Method == HttpMethod.Post && c.Path.EndsWith("/media_publish")) return (HttpStatusCode.OK, "{\"id\":\"m99\"}");
            if (c.Path.EndsWith("/m99")) return (HttpStatusCode.OK, "{\"permalink\":\"https://www.instagram.com/p/XYZ/\"}");
            if (c.Query.Contains("status_code")) return (HttpStatusCode.OK, ++_checks <= StatusChecksBeforeFinished ? "{\"status_code\":\"IN_PROGRESS\"}" : $"{{\"status_code\":\"{StatusCode}\",\"status\":\"Error: bad video\"}}");
            return (HttpStatusCode.OK, "{}");
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, false); }

    private static (InstagramGraph Ig, Graph Graph, MemStorage Storage) Make()
    {
        var storage = new MemStorage();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:PublicBaseUrl"] = "https://app.test", ["Messaging:LinkSecret"] = "secret", ["Publishing:Instagram:PollSeconds"] = "0.01", ["Publishing:Instagram:MaxWaitSeconds"] = "1",
        }).Build();
        var graph = new Graph(storage);
        return (new InstagramGraph(new Factory(graph), cfg, new PublicMedia(storage, cfg, new Clock(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero)))), graph, storage);
    }

    private static byte[] PngBytes(int w, int h) { using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)); bmp.Erase(new SKColor(10, 120, 200, 128)); using var img = SKImage.FromBitmap(bmp); using var data = img.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray(); }
    private static MediaFile Picture(int w = 1000, int h = 1000, string name = "pic.png") { var b = PngBytes(w, h); return new MediaFile(name, "image/png", b.Length, _ => Task.FromResult<Stream>(new MemoryStream(b))); }
    private static MediaFile Video(string name = "clip.mp4", string type = "video/mp4") => new(name, type, 6, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4, 5, 6])));
    private static PostRequest Req(string text, string format, params MediaFile[] media) => new("17841400000000001", "page-token", text, media, format);

    // ---- publishing ----

    [Fact]
    public async Task One_picture_is_converted_to_JPEG_offered_at_a_signed_address_published_and_the_copy_removed()
    {
        var (ig, graph, storage) = Make();
        var result = await ig.PublishAsync(Req("Hello Instagram #ludo", "Post", Picture()), default);

        Assert.Equal("m99", result.ProviderPostId); Assert.Equal("https://www.instagram.com/p/XYZ/", result.Url);
        var create = graph.Calls.First(c => c.Path.EndsWith("/17841400000000001/media"));
        Assert.Equal("Hello Instagram #ludo", create.Form["caption"]);
        Assert.Matches(@"^https://app\.test/m/[0-9a-f]{32}\.jpg\?e=\d+&s=[0-9a-f]{64}$", create.Form["image_url"]);
        Assert.Equal("page-token", create.Form["access_token"]);
        Assert.Contains("public-tmp/", graph.StoredAtCall[graph.Calls.IndexOf(create)]);   // the copy existed while Instagram fetched it
        Assert.Equal("c1", graph.Calls.Single(c => c.Path.EndsWith("/media_publish")).Form["creation_id"]);
        Assert.Empty(storage.Files);                                                          // and it is gone afterwards
    }

    [Fact]
    public async Task The_stored_copy_of_a_picture_really_is_a_JPEG_no_larger_than_1440_pixels()
    {
        var (ig, graph, storage) = Make();
        byte[]? seen = null;
        graph.Override = c => { if (c.Path.EndsWith("/media") && c.Method == HttpMethod.Post) seen = storage.Files.Values.Single(); return null; };
        await ig.PublishAsync(Req("x", "Post", Picture(2400, 2000)), default);
        Assert.NotNull(seen); Assert.Equal([0xFF, 0xD8, 0xFF], seen!.Take(3));
        using var decoded = SKBitmap.Decode(seen);
        Assert.Equal(1440, decoded.Width); Assert.True(decoded.Height <= 1440);
    }

    [Fact]
    public async Task Several_pictures_become_a_carousel_of_children_and_one_parent()
    {
        var (ig, graph, _) = Make();
        await ig.PublishAsync(Req("Three shots", "Post", Picture(), Picture(1200, 1000), Picture(1000, 1200)), default);
        var creates = graph.Calls.Where(c => c.Method == HttpMethod.Post && c.Path.EndsWith("/media")).ToList();
        Assert.Equal(4, creates.Count);
        Assert.All(creates.Take(3), c => Assert.Equal("true", c.Form["is_carousel_item"]));
        Assert.Equal("CAROUSEL", creates[3].Form["media_type"]); Assert.Equal("c1,c2,c3", creates[3].Form["children"]); Assert.Equal("Three shots", creates[3].Form["caption"]);
        Assert.Equal("c4", graph.Calls.Single(c => c.Path.EndsWith("/media_publish")).Form["creation_id"]);
    }

    [Fact]
    public async Task A_Reel_sends_the_video_address_waits_while_Instagram_processes_it_and_then_publishes()
    {
        var (ig, graph, storage) = Make();
        graph.StatusChecksBeforeFinished = 2;
        var result = await ig.PublishAsync(Req("Watch this", "Reel", Video()), default);
        var create = graph.Calls.First(c => c.Path.EndsWith("/media"));
        Assert.Equal("REELS", create.Form["media_type"]); Assert.Equal("true", create.Form["share_to_feed"]);
        Assert.Matches(@"^https://app\.test/m/[0-9a-f]{32}\.mp4\?e=\d+&s=", create.Form["video_url"]);
        Assert.True(graph.Calls.Count(c => c.Query.Contains("status_code")) >= 3);             // it asked until it was FINISHED
        Assert.Equal("m99", result.ProviderPostId); Assert.Empty(storage.Files);
    }

    [Fact]
    public async Task A_Story_takes_one_file_and_a_video_in_a_Post_is_refused()
    {
        var (ig, graph, _) = Make();
        await ig.PublishAsync(Req("", "Story", Picture()), default);
        Assert.Equal("STORIES", graph.Calls.First(c => c.Path.EndsWith("/media")).Form["media_type"]);
        var e = await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", "Post", Video()), default));
        Assert.Equal(PublishErrorKind.Permanent, e.Kind); Assert.Contains("Reel", e.Message);
    }

    [Theory]
    [InlineData("Post", 0, "picture or a video")]
    [InlineData("Reel", 2, "exactly one video")]
    public async Task Posts_that_Instagram_cannot_take_are_refused_before_anything_is_sent(string format, int pictures, string expected)
    {
        var (ig, graph, _) = Make();
        var media = Enumerable.Range(0, pictures).Select(_ => Picture()).ToArray();
        var e = await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", format, media), default));
        Assert.Equal(PublishErrorKind.Permanent, e.Kind); Assert.Contains(expected, e.Message);
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public async Task A_picture_that_is_too_wide_or_too_tall_is_refused_with_the_reason_and_leaves_no_copy()
    {
        var (ig, graph, storage) = Make();
        var wide = await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", "Post", Picture(2000, 800)), default));
        Assert.Contains("1.91:1", wide.Message);
        var tall = await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", "Post", Picture(600, 1200)), default));
        Assert.Contains("4:5", tall.Message);
        Assert.Empty(graph.Calls); Assert.Empty(storage.Files);
    }

    [Fact]
    public async Task More_than_thirty_hashtags_and_non_MP4_videos_are_refused()
    {
        var (ig, _, _) = Make();
        var tags = string.Join(" ", Enumerable.Range(0, 31).Select(i => "#t" + i));
        Assert.Contains("30 hashtags", (await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req(tags, "Post", Picture()), default))).Message);
        Assert.Contains("MP4", (await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", "Reel", Video("a.webm", "video/webm")), default))).Message);
    }

    [Fact]
    public async Task When_Instagram_refuses_the_media_the_reason_is_reported_and_nothing_is_published()
    {
        var (ig, graph, storage) = Make();
        graph.StatusCode = "ERROR";
        var e = await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", "Reel", Video()), default));
        Assert.Equal(PublishErrorKind.Permanent, e.Kind); Assert.Contains("bad video", e.Message);
        Assert.DoesNotContain(graph.Calls, c => c.Path.EndsWith("/media_publish"));
        Assert.Empty(storage.Files);
    }

    [Fact]
    public async Task Expired_permission_and_rate_limits_are_told_apart()
    {
        var (ig, graph, _) = Make();
        graph.Override = c => c.Path.EndsWith("/media") ? (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Invalid OAuth access token\",\"code\":190}}") : null;
        Assert.Equal(PublishErrorKind.AuthExpired, (await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", "Post", Picture()), default))).Kind);
        graph.Override = c => c.Path.EndsWith("/media") ? (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Application request limit reached\",\"code\":4}}") : null;
        Assert.Equal(PublishErrorKind.Retryable, (await Assert.ThrowsAsync<PublishException>(() => ig.PublishAsync(Req("x", "Post", Picture()), default))).Kind);
    }

    [Fact]
    public async Task A_recent_post_with_the_same_caption_is_found_after_an_uncertain_failure()
    {
        var (ig, graph, _) = Make();
        var now = DateTimeOffset.UtcNow;
        graph.Override = c => c.Path.EndsWith("/media") && c.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, $"{{\"data\":[{{\"id\":\"old\",\"caption\":\"Other\",\"timestamp\":\"{now:o}\"}},{{\"id\":\"m1\",\"caption\":\"Same words\",\"permalink\":\"https://www.instagram.com/p/AAA/\",\"timestamp\":\"{now.AddMinutes(-3):o}\"}}]}}") : null;
        var found = await ig.FindRecentAsync(Req("Same words", "Post"), default);
        Assert.Equal("m1", found?.ProviderPostId);
        Assert.Null(await ig.FindRecentAsync(Req("Never posted", "Post"), default));
    }

    [Fact]
    public async Task Post_figures_use_what_Instagram_gives_and_leave_the_rest_null_never_zero()
    {
        var (ig, graph, _) = Make();
        graph.Override = c =>
        {
            if (c.Query.Contains("like_count")) return (HttpStatusCode.OK, "{\"like_count\":12,\"comments_count\":3}");
            if (c.Query.Contains("metric=reach")) return (HttpStatusCode.OK, "{\"data\":[{\"name\":\"reach\",\"values\":[{\"value\":240}]}]}");
            if (c.Query.Contains("metric=shares")) return (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"metric shares is not supported\",\"code\":100}}");
            if (c.Query.Contains("metric=views")) return (HttpStatusCode.OK, "{\"data\":[{\"name\":\"views\",\"values\":[{\"value\":800}]}]}");
            return null;
        };
        var m = await ig.GetPostMetricsAsync(Req("", "Post"), "m99", default);
        Assert.Equal(12, m!.Reactions); Assert.Equal(3, m.Comments); Assert.Equal(240, m.Reach); Assert.Equal(800, m.Views);
        Assert.Null(m.Shares); Assert.Null(m.Clicks);
        Assert.Contains("shares", m.Note);
    }

    [Fact]
    public async Task Account_checks_report_a_dead_permission_as_not_valid()
    {
        var (ig, graph, _) = Make();
        Assert.True(await ig.IsAccountValidAsync("17841400000000001", "t", default));
        graph.Override = _ => (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"expired\",\"code\":190}}");
        Assert.False(await ig.IsAccountValidAsync("17841400000000001", "t", default));
    }

    // ---- signed public media ----

    [Fact]
    public async Task A_signed_address_serves_the_copy_until_it_expires_and_a_changed_address_serves_nothing()
    {
        var storage = new MemStorage(); var clock = new Clock(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Messaging:PublicBaseUrl"] = "https://app.test", ["Messaging:LinkSecret"] = "secret" }).Build();
        var media = new PublicMedia(storage, cfg, clock);
        var stored = await media.StoreAsync(new MemoryStream([9, 9, 9]), "jpg", default);
        var uri = new Uri(stored.Url); var name = uri.AbsolutePath.Split('/').Last();
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query); var e = long.Parse(q["e"]!); var s = q["s"]!;

        var ok = await media.OpenAsync(name, e, s, default);
        Assert.NotNull(ok); Assert.Equal("image/jpeg", ok!.Value.ContentType);
        Assert.Null(await media.OpenAsync(name, e, s.Replace(s[0], s[0] == 'a' ? 'b' : 'a'), default));                 // wrong signature
        Assert.Null(await media.OpenAsync(name, e + 100000, s, default));                                                 // a longer life than was signed
        Assert.Null(await media.OpenAsync("../" + name, e, s, default));                                                  // a path instead of a name
        Assert.Null(await media.OpenAsync(name.Replace(".jpg", ".png"), e, s, default));                                  // another type
        clock.UtcNow = clock.UtcNow.AddHours(3);
        Assert.Null(await media.OpenAsync(name, e, s, default));                                                          // expired
    }

    [Fact]
    public async Task Without_a_public_address_nothing_is_stored_and_the_reason_is_clear()
    {
        var media = new PublicMedia(new MemStorage(), new ConfigurationBuilder().Build(), new Clock(DateTimeOffset.UtcNow));
        var e = await Assert.ThrowsAsync<PublishException>(() => media.StoreAsync(new MemoryStream([1]), "jpg", default));
        Assert.Contains("public address", e.Message);
    }

    // ---- media rules for the channel ----

    [Fact]
    public void Instagram_posts_need_a_picture_and_a_video_belongs_in_a_Reel()
    {
        var pid = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        AssetInfo Pic() => new(Guid.NewGuid(), pid, "p.jpg", "image/jpeg", 10, false, "Unrestricted", null, false, null, null, null);
        AssetInfo Vid() => new(Guid.NewGuid(), pid, "v.mp4", "video/mp4", 10, false, "Unrestricted", null, false, 20, 1080, 1920);
        Assert.Contains(MediaRules.Validate("Post", [], pid, now, true, "Instagram"), x => x.Contains("at least one picture"));
        Assert.Empty(MediaRules.Validate("Post", [], pid, now, true, "Facebook"));                       // Facebook still allows text-only
        Assert.Empty(MediaRules.Validate("Post", [Pic()], pid, now, true, "Instagram"));
        Assert.Contains(MediaRules.Validate("Post", [Vid()], pid, now, true, "instagram"), x => x.Contains("Reel"));
        Assert.Empty(MediaRules.Validate("Reel", [Vid()], pid, now, true, "Instagram"));
        Assert.Empty(MediaRules.Validate("Post", [], pid, now, false, "Instagram"));                     // a draft being built is not complete yet
    }
}
