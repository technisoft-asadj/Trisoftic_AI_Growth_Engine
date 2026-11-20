using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>The autopilot choosing text, a picture or a video for each post: the owner's uploads first, AI cartoon pictures next, text when neither works.</summary>
[Collection("host")]
public sealed class AutopilotMediaTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private static string Letters() => new(Uniq().Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"apm-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private async Task Approve(HttpClient admin, HttpClient approver, string subjectId)
    {
        var ap = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={subjectId}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{ap}/decision", new { decision = "Approved" })).EnsureSuccessStatusCode();
    }

    private async Task<Guid> ProductAsync(HttpClient admin, HttpClient approver)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Pharmacy POS").GetProperty("id").GetGuid();
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Vis " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new
        {
            content = new
            {
                summary = "Point of sale for pharmacies",
                facts = new object[] { new { id = "f-offline", category = "Feature", text = "Works offline", evidence = "Spec", verified = true } },
                claims = Array.Empty<object>(), markets = new[] { new { country = "PK", languages = new[] { "en" } } },
            },
        });
        (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null)).EnsureSuccessStatusCode();
        await Approve(admin, approver, pid.ToString());
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Vis page" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static void Script(params string[] r) { FakeAiProvider.Script.Clear(); foreach (var x in r) FakeAiProvider.Script.Enqueue(x); FakeAiProvider.OutputTokens = 500; FakeAiProvider.Prompts.Clear(); lock (FakeImageProvider.Calls) FakeImageProvider.Calls.Clear(); }

    private static string PlanJson(string visual, string? imageBrief = null) => JsonSerializer.Serialize(new
    {
        rationale = "A short run that introduces the product.",
        measures = new[] { new { name = "Reactions", why = "shows interest" } },
        slots = new[] { new { angle = "Angle " + Letters(), brief = "Cover a verified fact.", hookType = "Question", visual, imageBrief } },
    });

    private static string PostJson(string body) => JsonSerializer.Serialize(new { title = "Offline", body, claims = new[] { new { text = "Works offline", factRef = "f-offline", assumption = false } }, questions = Array.Empty<string>() });
    private const string Good = "Our pharmacy POS keeps working offline.";
    private const string Approve1 = """{"verdict":"approve","issues":[],"summary":"Grounded and in voice."}""";

    private static Task SetAsync(HttpClient c, Guid pid, bool images = true, bool videos = true)
        => c.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", new { enabled = true, channel = "facebook", goal = "Introduce the product", planDays = 3, everyDays = 3, postTimeUtc = "13:00", maxPostsPerDay = 2, lookaheadHours = 96, useImages = images, useVideos = videos });

    private static async Task<string> RunAsync(HttpClient c, Guid pid)
    {
        var r = await c.PostAsync($"/api/v1/products/{pid}/autopilot/run", null);
        var body = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, body);
        return string.Join(" ", JsonSerializer.Deserialize<JsonElement>(body, Json).GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    private static async Task<JsonElement> PlanAsync(HttpClient c, Guid pid)
    {
        var id = (await c.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/plans", Json)).EnumerateArray().First().GetProperty("id").GetGuid();
        return await c.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}", Json);
    }

    private static async Task<JsonElement> ItemAsync(HttpClient c, JsonElement plan) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/content/{plan.GetProperty("slots")[0].GetProperty("contentItemId").GetGuid()}", Json);

    private static byte[] Png(byte marker) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray(); var b = new byte[8 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length); System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4); body.CopyTo(b, 8); return b;
    }
    private static byte[] U32(uint v) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Clip(int w, int h, uint seconds, byte marker)
    {
        var matrix = new byte[36]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(matrix.AsSpan(0), 0x10000); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(matrix.AsSpan(32), 0x40000000);
        var tkhd = Box("tkhd", new byte[4], new byte[8], U32(1), new byte[4], new byte[4], new byte[8], new byte[8], matrix, U32((uint)w << 16), U32((uint)h << 16));
        var hdlr = Box("hdlr", new byte[4], new byte[4], System.Text.Encoding.ASCII.GetBytes("vide"), new byte[12], new byte[1]);
        var moov = Box("moov", Box("mvhd", new byte[4], new byte[8], U32(1000), U32(seconds * 1000), new byte[80]), Box("trak", tkhd, Box("mdia", hdlr)));
        return [.. Box("ftyp", System.Text.Encoding.ASCII.GetBytes("mp42"), new byte[4], System.Text.Encoding.ASCII.GetBytes("mp42isom")), .. Box("mdat", Enumerable.Repeat(marker, 3000).ToArray()), .. moov];
    }

    private static async Task<Guid> UploadAsync(HttpClient admin, Guid pid, byte[] bytes, string name, string kind, string rights = "Unrestricted")
    {
        var f = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        f.Add(file, "file", name); f.Add(new StringContent(kind), "kind"); f.Add(new StringContent("Own"), "source"); f.Add(new StringContent(rights), "rights");
        var r = await admin.PostAsync($"/api/v1/products/{pid}/assets", f);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task With_no_uploads_the_autopilot_makes_an_ai_cartoon_picture_attaches_it_and_the_reviewer_is_told_what_it_is()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        await SetAsync(admin, pid);
        Script(PlanJson("image", "A dice rolling across a colourful board"), PostJson(Good), Approve1);
        var notes = await RunAsync(admin, pid);
        Assert.Contains("AI cartoon picture", notes); Assert.Contains("Scheduled 1", notes);
        Assert.Single(FakeImageProvider.Calls);

        var plan = await PlanAsync(admin, pid);
        Assert.Equal("image", plan.GetProperty("slots")[0].GetProperty("visual").GetString());
        var item = await ItemAsync(admin, plan);
        var media = item.GetProperty("current").GetProperty("media");
        Assert.Equal(1, media.GetArrayLength()); Assert.StartsWith("ai-", media[0].GetProperty("name").GetString());
        Assert.Equal("Post", item.GetProperty("item").GetProperty("format").GetString());
        Assert.Equal("Scheduled", item.GetProperty("item").GetProperty("status").GetString());
        Assert.Contains(FakeAiProvider.Prompts, p => p.Contains("not a screenshot or real gameplay"));                 // the writer is told it is an illustration
        Assert.Contains(FakeAiProvider.Prompts, p => p.Contains("AI-generated cartoon-style illustration"));            // and so is the reviewer
    }

    [Fact]
    public async Task A_planner_picture_idea_with_people_is_sent_back_and_the_corrected_plan_is_used()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        await SetAsync(admin, pid);
        Script(PlanJson("image", "Friends smiling around a table"), PlanJson("image", "A trophy on a podium made of game pieces"), PostJson(Good), Approve1);
        await RunAsync(admin, pid);
        Assert.Contains("not allowed", FakeAiProvider.Prompts[1], StringComparison.OrdinalIgnoreCase);                  // the planner was told why
        Assert.Equal("A trophy on a podium made of game pieces", (await PlanAsync(admin, pid)).GetProperty("slots")[0].GetProperty("imageBrief").GetString());
    }

    [Fact]
    public async Task An_uploaded_picture_is_used_before_the_ai_makes_one_and_the_same_file_is_not_used_twice()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        var own = await UploadAsync(admin, pid, Png(7), "own-" + Letters() + ".png", "Screenshot");
        await SetAsync(admin, pid);
        Script(PlanJson("image", "A dice rolling across a colourful board"), PostJson(Good), Approve1);
        await RunAsync(admin, pid);
        Assert.Empty(FakeImageProvider.Calls);                                                                           // no AI picture was needed
        var item = await ItemAsync(admin, await PlanAsync(admin, pid));
        Assert.Equal(own, item.GetProperty("current").GetProperty("assetIds")[0].GetGuid());

        // the file is now on a live post, so the next image post falls back to an AI picture
        Assert.Equal(1, item.GetProperty("current").GetProperty("assetIds").GetArrayLength());
    }

    [Fact]
    public async Task A_free_vertical_video_becomes_a_reel_and_the_planner_is_told_how_many_it_may_use()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        var video = await UploadAsync(admin, pid, Clip(1080, 1920, 15, 3), "clip-" + Letters() + ".mp4", "GameplayFootage");
        await SetAsync(admin, pid);
        Script(PlanJson("video"), PostJson(Good), Approve1);
        var notes = await RunAsync(admin, pid);
        Assert.Contains("Reel from your video", notes);
        Assert.Contains("at most 1 time(s)", FakeAiProvider.Prompts[0]);
        var item = await ItemAsync(admin, await PlanAsync(admin, pid));
        Assert.Equal("Reel", item.GetProperty("item").GetProperty("format").GetString());
        Assert.Equal(video, item.GetProperty("current").GetProperty("assetIds")[0].GetGuid());
        Assert.Equal("Scheduled", item.GetProperty("item").GetProperty("status").GetString());
        Assert.Empty(FakeImageProvider.Calls);
    }

    [Fact]
    public async Task A_planner_that_asks_for_a_video_when_none_is_available_is_sent_back()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        await SetAsync(admin, pid);
        Script(PlanJson("video"), PlanJson("text"), PostJson(Good), Approve1);
        await RunAsync(admin, pid);
        Assert.Contains("never use \"video\"", FakeAiProvider.Prompts[0]);
        Assert.Contains("only 0 uploaded video", FakeAiProvider.Prompts[1]);
    }

    [Fact]
    public async Task When_the_ai_picture_cannot_be_made_the_post_goes_out_as_text_with_a_note()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        await SetAsync(admin, pid);
        FakeImageProvider.FailWith = "refuse";
        try
        {
            Script(PlanJson("image", "A dice rolling across a colourful board"), PostJson(Good), Approve1);
            var notes = await RunAsync(admin, pid);
            Assert.Contains("this is text only", notes); Assert.Contains("Scheduled 1", notes);
            Assert.Equal(0, (await ItemAsync(admin, await PlanAsync(admin, pid))).GetProperty("current").GetProperty("assetIds").GetArrayLength());
        }
        finally { FakeImageProvider.FailWith = null; }
    }

    [Fact]
    public async Task With_pictures_and_videos_switched_off_every_post_is_text_and_the_planner_is_not_offered_visuals()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        await UploadAsync(admin, pid, Png(9), "own-" + Letters() + ".png", "Screenshot");
        await SetAsync(admin, pid, images: false, videos: false);
        Script(PlanJson("image", "A dice rolling across a colourful board"), PostJson(Good), Approve1);
        await RunAsync(admin, pid);
        Assert.DoesNotContain("Give every slot a \"visual\"", FakeAiProvider.Prompts[0]);
        Assert.Empty(FakeImageProvider.Calls);
        var item = await ItemAsync(admin, await PlanAsync(admin, pid));
        Assert.Equal(0, item.GetProperty("current").GetProperty("assetIds").GetArrayLength());
    }

    [Fact]
    public async Task Text_that_passes_an_ai_illustration_off_as_a_real_screenshot_is_rejected_by_the_checks()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        await SetAsync(admin, pid);
        Script(PlanJson("image", "A dice rolling across a colourful board"), PostJson("Here is a real gameplay screenshot of our POS working offline."));   // no reviewer reply is scripted: the checks reject first
        await RunAsync(admin, pid);
        var st = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/autopilot", Json);
        Assert.Equal("Rejected", st.GetProperty("decisions")[0].GetProperty("verdict").GetString());
        Assert.Contains("illustration", st.GetProperty("decisions")[0].GetProperty("reasons").GetString());
    }
}
