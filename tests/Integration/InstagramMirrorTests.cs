using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>The autopilot copying approved picture and video posts to Instagram: same words and media, the fixed checks again, its own tracked link, an hour later, never twice.</summary>
[Collection("host")]
public sealed class InstagramMirrorTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private static string Letters() => new(Uniq().Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
    private const string Store = "https://play.google.com/store/apps/details?id=com.example.app";

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"im-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private async Task Approve(HttpClient admin, HttpClient approver, string subjectId)
    {
        var ap = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={subjectId}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{ap}/decision", new { decision = "Approved" })).EnsureSuccessStatusCode();
    }

    private async Task<Guid> ProductAsync(HttpClient admin, HttpClient approver, bool withInstagram)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Pharmacy POS").GetProperty("id").GetGuid();
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Mir " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new
        {
            content = new
            {
                summary = "Point of sale for pharmacies",
                facts = new object[]
                {
                    new { id = "f-offline", category = "Feature", text = "Works offline", evidence = "Spec", verified = true },
                    new { id = "f-store", category = "Platform", text = "Get it at " + Store, evidence = "Store page", verified = true },
                },
                claims = Array.Empty<object>(), markets = new[] { new { country = "PK", languages = new[] { "en" } } },
            },
        });
        (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null)).EnsureSuccessStatusCode();
        await Approve(admin, approver, pid.ToString());
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Mir page" })).EnsureSuccessStatusCode();
        if (withInstagram) (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "Instagram", name = "@mir_test" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static void Script(params string[] r) { FakeAiProvider.Script.Clear(); foreach (var x in r) FakeAiProvider.Script.Enqueue(x); FakeAiProvider.OutputTokens = 500; FakeAiProvider.Prompts.Clear(); }

    private static string PlanJson(string visual) => JsonSerializer.Serialize(new
    {
        rationale = "A short run.", measures = new[] { new { name = "Reactions", why = "interest" } },
        slots = new[] { new { angle = "Angle " + Letters(), brief = "Cover a verified fact.", hookType = "Question", visual, imageBrief = visual == "image" ? "A dice rolling across a colourful board" : null } },
    });
    private static string PostJson() => JsonSerializer.Serialize(new { title = "Offline", body = "Our pharmacy POS keeps working offline.\n\n" + Store, claims = new[] { new { text = "Works offline", factRef = "f-offline", assumption = false } }, questions = Array.Empty<string>() });
    private const string Approve1 = """{"verdict":"approve","issues":[],"summary":"Grounded and in voice."}""";

    private static Task<HttpResponseMessage> SetAsync(HttpClient c, Guid pid, bool alsoInstagram, bool images = true)
        => c.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", new { enabled = true, channel = "facebook", goal = "Introduce the product", planDays = 3, everyDays = 3, postTimeUtc = "13:00", maxPostsPerDay = 2, lookaheadHours = 96, useImages = images, useVideos = false, alsoInstagram });

    private static async Task<string> RunAsync(HttpClient c, Guid pid)
    {
        var r = await c.PostAsync($"/api/v1/products/{pid}/autopilot/run", null);
        var body = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, body);
        return string.Join(" ", JsonSerializer.Deserialize<JsonElement>(body, Json).GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    private static async Task<Guid> UploadPictureAsync(HttpClient admin, Guid pid)
    {
        var f = new MultipartFormDataContent();
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 2, 3, 4, 5, 6, 7, 8, 9, 10]); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        f.Add(file, "file", "own-screenshot.png"); f.Add(new StringContent("Screenshot"), "kind"); f.Add(new StringContent("Own"), "source"); f.Add(new StringContent("Unrestricted"), "rights");
        var r = await admin.PostAsync($"/api/v1/products/{pid}/assets", f);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient c, Guid pid) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/v1/content?productId={pid}&take=50", Json)).EnumerateArray().ToList();

    [Fact]
    public async Task An_approved_picture_post_is_copied_to_Instagram_checked_again_approved_and_scheduled_an_hour_later_with_its_own_link_and_never_twice()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver, withInstagram: true);
        var asset = await UploadPictureAsync(admin, pid);
        (await SetAsync(admin, pid, alsoInstagram: true)).EnsureSuccessStatusCode();

        Script(PlanJson("image"), PostJson(), Approve1);
        var first = await RunAsync(admin, pid);
        Assert.Contains("Scheduled 1 post(s)", first); Assert.Contains("Made the Instagram copy of post 1", first);
        Assert.Contains("Approved an Instagram copy", first); Assert.Contains("Scheduled 1 Instagram post(s)", first);
        Assert.Equal(3, FakeAiProvider.Prompts.Count);              // plan, post and the Facebook review: the copy needed no model call of its own

        Script();
        Assert.Equal("", (await RunAsync(admin, pid)).Trim());      // nothing left to do, and still no model calls
        Assert.Empty(FakeAiProvider.Prompts);

        var items = await ItemsAsync(admin, pid);
        var fb = items.Single(i => i.GetProperty("channel").GetString() != "Instagram" && i.GetProperty("status").GetString() != "Cancelled");
        var ig = items.Single(i => i.GetProperty("channel").GetString() == "Instagram");
        Assert.Equal("Scheduled", ig.GetProperty("status").GetString());
        Assert.Equal(DateTimeOffset.Parse(fb.GetProperty("scheduledFor").GetString()!).AddHours(1), DateTimeOffset.Parse(ig.GetProperty("scheduledFor").GetString()!));

        var igFull = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{ig.GetProperty("id").GetGuid()}", Json);
        var fbFull = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{fb.GetProperty("id").GetGuid()}", Json);
        Assert.Equal("Post", igFull.GetProperty("item").GetProperty("format").GetString());
        Assert.Equal(asset, igFull.GetProperty("current").GetProperty("assetIds")[0].GetGuid());
        var fbBody = fbFull.GetProperty("current").GetProperty("body").GetString()!; var igBody = igFull.GetProperty("current").GetProperty("body").GetString()!;
        var fbLink = System.Text.RegularExpressions.Regex.Match(fbBody, @"https://app\.test/r/\w+").Value; var igLink = System.Text.RegularExpressions.Regex.Match(igBody, @"https://app\.test/r/\w+").Value;
        Assert.NotEmpty(fbLink); Assert.NotEmpty(igLink); Assert.NotEqual(fbLink, igLink);                    // Instagram clicks are counted on their own link
        Assert.Equal(fbBody.Replace(fbLink, igLink), igBody);                                                  // otherwise the words are identical

        var visitor = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var click = new HttpRequestMessage(HttpMethod.Get, "/r/" + igLink[(igLink.LastIndexOf('/') + 1)..]);
        click.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 13) Chrome/120 Mobile Safari/537.36");
        Assert.Contains("utm_source%3Dinstagram", (await visitor.SendAsync(click)).Headers.Location!.ToString());

        await RunAsync(admin, pid);                                  // another pass makes no second copy
        Assert.Single(await ItemsAsync(admin, pid), i => i.GetProperty("channel").GetString() == "Instagram");
    }

    [Fact]
    public async Task A_post_with_no_picture_or_video_is_skipped_with_a_reason_and_not_retried()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver, withInstagram: true);
        (await SetAsync(admin, pid, alsoInstagram: true, images: false)).EnsureSuccessStatusCode();
        Script(PlanJson("text"), PostJson(), Approve1);
        Assert.Contains("Scheduled 1 post(s)", await RunAsync(admin, pid));
        Script();
        await RunAsync(admin, pid); await RunAsync(admin, pid);
        Assert.DoesNotContain(await ItemsAsync(admin, pid), i => i.GetProperty("channel").GetString() == "Instagram");
    }

    [Fact]
    public async Task Nothing_is_copied_when_the_switch_is_off_or_no_Instagram_account_is_connected()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var off = await ProductAsync(admin, approver, withInstagram: true);
        await UploadPictureAsync(admin, off);
        (await SetAsync(admin, off, alsoInstagram: false)).EnsureSuccessStatusCode();
        Script(PlanJson("image"), PostJson(), Approve1);
        await RunAsync(admin, off); Script(); await RunAsync(admin, off);
        Assert.DoesNotContain(await ItemsAsync(admin, off), i => i.GetProperty("channel").GetString() == "Instagram");

        var noAccount = await ProductAsync(admin, approver, withInstagram: false);
        await UploadPictureAsync(admin, noAccount);
        (await SetAsync(admin, noAccount, alsoInstagram: true)).EnsureSuccessStatusCode();
        Script(PlanJson("image"), PostJson(), Approve1);
        await RunAsync(admin, noAccount); Script(); await RunAsync(admin, noAccount);
        Assert.DoesNotContain(await ItemsAsync(admin, noAccount), i => i.GetProperty("channel").GetString() == "Instagram");
    }
}
