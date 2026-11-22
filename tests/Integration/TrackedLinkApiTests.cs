using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>The store link in a post becomes a short link that counts clicks; previews and robots are not clicks; the report says clicks, never installs.</summary>
[Collection("host")]
public sealed class TrackedLinkApiTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private static string Letters() => new(Uniq().Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
    private const string Store = "https://play.google.com/store/apps/details?id=com.example.app";
    private const string Phone = "Mozilla/5.0 (Linux; Android 13; SM-A135F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"tl-{Uniq()}@test.local";
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
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Lnk " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
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
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Lnk page" })).EnsureSuccessStatusCode();
        return pid;
    }

    private HttpClient Visitor() => host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> Click(HttpClient c, string code, string ua, string ip)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/r/" + code);
        r.Headers.TryAddWithoutValidation("User-Agent", ua);
        r.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return await c.SendAsync(r);
    }

    [Fact]
    public async Task The_store_link_in_an_autopilot_post_becomes_a_tracked_link_the_reviewer_accepts_and_clicks_are_counted_per_post()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", new { enabled = true, channel = "facebook", goal = "Introduce the product", planDays = 3, everyDays = 3, postTimeUtc = "13:00", maxPostsPerDay = 2, lookaheadHours = 96, useImages = false, useVideos = false })).EnsureSuccessStatusCode();

        FakeAiProvider.Script.Clear();
        FakeAiProvider.Script.Enqueue(JsonSerializer.Serialize(new { rationale = "A short run.", measures = new[] { new { name = "Reactions", why = "interest" } }, slots = new[] { new { angle = "Angle " + Letters(), brief = "Cover a verified fact.", hookType = "Question" } } }));
        FakeAiProvider.Script.Enqueue(JsonSerializer.Serialize(new { title = "Offline", body = "Our pharmacy POS keeps working offline.\n\n" + Store, claims = new[] { new { text = "Works offline", factRef = "f-offline", assumption = false } }, questions = Array.Empty<string>() }));
        FakeAiProvider.Script.Enqueue("""{"verdict":"approve","issues":[],"summary":"Grounded and in voice."}""");
        FakeAiProvider.OutputTokens = 500; FakeAiProvider.Prompts.Clear();
        var run = await (await admin.PostAsync($"/api/v1/products/{pid}/autopilot/run", null)).Content.ReadAsStringAsync();
        Assert.Contains("Approved post 1", run);

        var planId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/plans", Json)).EnumerateArray().First().GetProperty("id").GetGuid();
        var itemId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("slots")[0].GetProperty("contentItemId").GetGuid();
        var body = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{itemId}", Json)).GetProperty("current").GetProperty("body").GetString()!;
        Assert.DoesNotContain("play.google.com", body);
        var code = Regex.Match(body, @"https://app\.test/r/([0-9A-Za-z]+)").Groups[1].Value;
        Assert.NotEmpty(code);

        var visitor = Visitor();
        var first = await Click(visitor, code, Phone, "203.0.113.7");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var location = first.Headers.Location!.ToString();
        Assert.StartsWith(Store + "&referrer=", location);
        Assert.Contains("utm_content%3D" + code, location);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await Click(visitor, code, "facebookexternalhit/1.1", "31.13.0.1")).StatusCode);   // a link preview still redirects but is not a click
        await Click(visitor, code, Phone, "203.0.113.7");                                                                        // the same person again
        await Click(visitor, code, Phone, "198.51.100.9");                                                                       // someone else

        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/links/report", Json);
        Assert.Equal(3, report.GetProperty("totalClicks").GetInt32());
        Assert.Equal(2, report.GetProperty("totalUnique").GetInt32());
        var post = report.GetProperty("byPost").EnumerateArray().Single();
        Assert.Equal(itemId, post.GetProperty("contentItemId").GetGuid());
        Assert.Equal(3, post.GetProperty("clicks").GetInt32());
        Assert.DoesNotContain("installed", report.GetProperty("note").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await Click(visitor, "nope1234", Phone, "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Click(visitor, "a!", Phone, "203.0.113.7")).StatusCode);
    }

    [Fact]
    public async Task A_person_can_make_a_loose_link_but_only_for_web_addresses_and_only_if_allowed()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        Assert.Equal(HttpStatusCode.NotFound, (await approver.PostAsJsonAsync($"/api/v1/products/{pid}/links", new { url = Store })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/links", new { url = "javascript:alert(1)" })).StatusCode);
        var made = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/links", new { url = Store, channel = "Instagram", label = "Instagram bio" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var link = await made.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.StartsWith("https://app.test/r/", link.GetProperty("shortUrl").GetString());
        var r = await Click(Visitor(), link.GetProperty("code").GetString()!, Phone, "203.0.113.50");
        Assert.Contains("utm_source%3Dinstagram", r.Headers.Location!.ToString());
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/links/report", Json);
        Assert.Equal(1, report.GetProperty("loose")[0].GetProperty("clicks").GetInt32());
        Assert.Equal(0, report.GetProperty("byPost").GetArrayLength());
    }
}
