using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>Photos, a video and stories: from attaching a file to what the network is handed, with the rules that keep an unapproved or unusable file from going out.</summary>
[Collection("host")]
public sealed class MediaTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();

    private static byte[] Png(byte marker) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private static byte[] Mp4() => [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0, 9, 9, 9, 9];

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"media-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Media " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        var pid = (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Media page" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static async Task<Guid> UploadAsync(HttpClient admin, Guid pid, byte[] bytes, string name, string rights = "Unrestricted")
    {
        var f = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        f.Add(file, "file", name); f.Add(new StringContent("Screenshot"), "kind"); f.Add(new StringContent("Own"), "source"); f.Add(new StringContent(rights), "rights");
        var r = await admin.PostAsync($"/api/v1/products/{pid}/assets", f);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient admin, Guid pid, string text, string format, params Guid[] assets)
        => await admin.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "Media post", channel = "facebook", format, body = text, assetIds = assets });

    private static async Task<Guid> IdOf(HttpResponseMessage r) { Assert.Equal(HttpStatusCode.Created, r.StatusCode); return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid(); }

    private static async Task ApproveAsync(HttpClient admin, HttpClient approver, Guid id)
    {
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/content/{id}/submit", null)).StatusCode);
        var ap = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={id}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/api/v1/approvals/{ap}/decision", new { decision = "Approved" })).StatusCode);
    }

    private async Task<string> PublishAsync(HttpClient admin, Guid id)
    {
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddMinutes(30) })).StatusCode);
        var runner = host.Services.GetRequiredService<JobRunner>();
        string status = "";
        for (var i = 0; i < 6; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(i == 0 ? 31 : 45));
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("publishing.dispatch", "{}", IdempotencyKey: "d-" + Guid.NewGuid()));
            for (var k = 0; k < 200 && await runner.RunOnceAsync(default); k++) { }
            status = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{id}", Json)).GetProperty("item").GetProperty("status").GetString()!;
            if (status is "Published" or "PublishFailed") break;
        }
        return status;
    }

    [Fact]
    public async Task Photos_a_story_and_the_rules_around_them_work_from_attaching_to_the_network()
    {
        var before = Clock.UtcNow;
        try { await RunAsync(); } finally { Clock.Set(before); }   // the clock is shared with every other test
    }

    private async Task RunAsync()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); var other = await ProductAsync(admin);
        var a = await UploadAsync(admin, pid, Png(1), "first.png"); var b = await UploadAsync(admin, pid, Png(2), "second.png");
        var secret = await UploadAsync(admin, pid, Png(3), "secret.png", rights: "InternalOnly");
        var foreign = await UploadAsync(admin, other, Png(4), "foreign.png");

        // ---- attaching: only usable files of this product ----
        var t1 = "Two photos " + Uniq();
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(admin, pid, t1, "Post", secret)).StatusCode);       // internal-only
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(admin, pid, t1, "Post", foreign)).StatusCode);      // another product's file
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(admin, pid, t1, "Blog", a)).StatusCode);            // a blog carries no media
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(admin, pid, t1, "Post", Guid.NewGuid())).StatusCode);   // no such file
        var post = await IdOf(await CreateAsync(admin, pid, t1, "Post", a, b));
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{post}", Json);
        Assert.Equal(new[] { "first.png", "second.png" }, detail.GetProperty("current").GetProperty("media").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToArray());

        // an edit that does not mention files keeps them; one that does replaces them
        (await admin.PostAsJsonAsync($"/api/v1/content/{post}/versions", new { body = t1, note = "same files", baseVersion = 1 })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{post}", Json)).GetProperty("current").GetProperty("assetIds").GetArrayLength());
        (await admin.PostAsJsonAsync($"/api/v1/content/{post}/versions", new { body = t1, note = "one file", baseVersion = 2, assetIds = new[] { b } })).EnsureSuccessStatusCode();
        Assert.Equal(b, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{post}", Json)).GetProperty("current").GetProperty("assetIds")[0].GetGuid());
        (await admin.PostAsJsonAsync($"/api/v1/content/{post}/versions", new { body = t1, note = "both, in this order", baseVersion = 3, assetIds = new[] { b, a } })).EnsureSuccessStatusCode();

        // ---- the network is handed the approved files, in order, with their real bytes ----
        await ApproveAsync(admin, approver, post);
        Assert.Equal("Published", await PublishAsync(admin, post));
        var sent = ScriptedPublisher.Sent[t1];
        Assert.Equal("Post", sent.Format);
        Assert.Equal(new[] { "second.png", "first.png" }, sent.Files.Select(f => f.Name).ToArray());
        Assert.Equal(Png(2), sent.Files[0].Bytes); Assert.Equal(Png(1), sent.Files[1].Bytes);
        Assert.All(sent.Files, f => Assert.Equal("image/png", f.Type));

        // ---- a story: may start empty, cannot be submitted without exactly one image ----
        var t2 = "Story " + Uniq();
        var story = await IdOf(await CreateAsync(admin, pid, t2, "Story"));
        var noImage = await admin.PostAsync($"/api/v1/content/{story}/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, noImage.StatusCode); Assert.Contains("exactly one image", await noImage.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/content/{story}/versions", new { body = t2, baseVersion = 1, assetIds = new[] { a, b } })).StatusCode);
        (await admin.PostAsJsonAsync($"/api/v1/content/{story}/versions", new { body = t2, note = "add the image", baseVersion = 1, assetIds = new[] { a } })).EnsureSuccessStatusCode();
        await ApproveAsync(admin, approver, story);
        Assert.Equal("Published", await PublishAsync(admin, story));
        Assert.Equal(("Story", 1), (ScriptedPublisher.Sent[t2].Format, ScriptedPublisher.Sent[t2].Files.Count));

        // ---- a file retired after approval is caught at the last moment and nothing goes out ----
        var t3 = "Retired later " + Uniq();
        var doomed = await UploadAsync(admin, pid, Png(7), "doomed.png");
        var late = await IdOf(await CreateAsync(admin, pid, t3, "Post", doomed));
        await ApproveAsync(admin, approver, late);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/{late}/schedule", new { at = Clock.UtcNow.AddMinutes(30) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/assets/{doomed}/retire/true", null)).StatusCode);
        Clock.Advance(TimeSpan.FromMinutes(31));
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("publishing.dispatch", "{}", IdempotencyKey: "d-" + Guid.NewGuid()));
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var k = 0; k < 200 && await runner.RunOnceAsync(default); k++) { }
        Assert.Equal("PublishFailed", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{late}", Json)).GetProperty("item").GetProperty("status").GetString());
        Assert.False(ScriptedPublisher.Sent.ContainsKey(t3));
        var failures = await admin.GetFromJsonAsync<JsonElement>("/api/v1/publishing/failures", Json);
        Assert.Contains(failures.EnumerateArray(), f => f.GetProperty("reason").GetString()!.Contains("retired"));
    }

    [Fact]
    public async Task A_video_post_carries_one_video_and_cannot_be_mixed_with_images()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var f = new MultipartFormDataContent();
        var file = new ByteArrayContent(Mp4()); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        f.Add(file, "file", "clip.mp4"); f.Add(new StringContent("GameplayFootage"), "kind"); f.Add(new StringContent("Own"), "source"); f.Add(new StringContent("Unrestricted"), "rights");
        var up = await admin.PostAsync($"/api/v1/products/{pid}/assets", f); Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var video = (await up.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var img = await UploadAsync(admin, pid, Png(5), "pic.png");
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(admin, pid, "Clip " + Uniq(), "Post", video)).StatusCode);
        var mixed = await CreateAsync(admin, pid, "Mixed " + Uniq(), "Post", video, img);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode); Assert.Contains("not both", await mixed.Content.ReadAsStringAsync());
    }
}
