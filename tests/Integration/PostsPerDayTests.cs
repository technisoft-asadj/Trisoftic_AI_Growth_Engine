using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>Alerts open when something is wrong and close by themselves; the weekly digest is written from recorded figures when a week has passed.</summary>
[Collection("host")]
public sealed class PostsPerDayTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private static string Letters() => new(Uniq().Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"op-{Uniq()}@test.local";
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
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Ops " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
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
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Ops page" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static void Script(params string[] r) { FakeAiProvider.Script.Clear(); foreach (var x in r) FakeAiProvider.Script.Enqueue(x); FakeAiProvider.OutputTokens = 500; FakeAiProvider.Prompts.Clear(); }
    private static string PlanJson() => JsonSerializer.Serialize(new
    {
        rationale = "A short run.", measures = new[] { new { name = "Reactions", why = "interest" } },
        slots = new[] { new { angle = "Angle " + Letters(), brief = "Cover a verified fact.", hookType = "Question" } },
    });
    private static string PostJson(string body) => JsonSerializer.Serialize(new { title = "Offline", body, claims = new[] { new { text = "Works offline", factRef = "f-offline", assumption = false } }, questions = Array.Empty<string>() });
    private const string Approve1 = """{"verdict":"approve","issues":[],"summary":"Grounded and in voice."}""";

    private static async Task<JsonElement> OpsAsync(HttpClient c, Guid pid) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/ops", Json);
    private static async Task<JsonElement> CheckAsync(HttpClient c, Guid pid)
    {
        var r = await c.PostAsync($"/api/v1/products/{pid}/ops/check", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static string PlanJson3() => JsonSerializer.Serialize(new
    {
        rationale = "Three posts a day.", measures = new[] { new { name = "Reactions", why = "interest" } },
        slots = new[] { "Question", "Tip", "Announcement" }.Select(h => new { angle = "Angle " + Letters(), brief = "Cover a verified fact.", hookType = h }).ToArray(),
    });

    [Fact]
    public async Task Three_posting_times_make_three_slots_on_the_day_and_bad_settings_are_refused()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        object Body(string[] times, int cap) => new { enabled = true, channel = "facebook", goal = "Introduce the product", planDays = 3, everyDays = 3, postTimesUtc = times, maxPostsPerDay = cap, lookaheadHours = 96, useImages = false, useVideos = false };

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", Body(["08:00", "09:00"], 3))).StatusCode);                 // under two hours apart
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", Body(["08:00", "13:00", "19:00"], 2))).StatusCode);     // daily ceiling below the number of times
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", Body(["01:00", "05:00", "09:00", "13:00"], 4))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", Body(["19:00", "08:00", "13:00"], 3))).StatusCode);

        var state = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/autopilot", Json);
        Assert.Equal(["08:00", "13:00", "19:00"], state.GetProperty("postTimesUtc").EnumerateArray().Select(x => x.GetString()));

        Script(PlanJson3());
        var run = await admin.PostAsync($"/api/v1/products/{pid}/autopilot/run", null);
        Assert.Contains("Planned 3 posts", await run.Content.ReadAsStringAsync());
        Assert.Contains("3 posts on every posting day", FakeAiProvider.Prompts[0]);
        var planId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/plans", Json)).EnumerateArray().First().GetProperty("id").GetGuid();
        var plan = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json);
        var hours = plan.GetProperty("slots").EnumerateArray().Select(x => DateTimeOffset.Parse(x.GetProperty("scheduledFor").GetString()!).Hour).ToList();
        Assert.Equal([8, 13, 19], hours);
        Assert.Single(plan.GetProperty("slots").EnumerateArray().Select(x => DateTimeOffset.Parse(x.GetProperty("scheduledFor").GetString()!).Date).Distinct());
    }
}
