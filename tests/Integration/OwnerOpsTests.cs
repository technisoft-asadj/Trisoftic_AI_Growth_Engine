using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>Alerts open when something is wrong and close by themselves; the weekly digest is written from recorded figures when a week has passed.</summary>
[Collection("host")]
public sealed class OwnerOpsTests(TestHost host)
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

    [Fact]
    public async Task A_stalled_autopilot_raises_an_alert_that_closes_when_it_runs_again_and_a_digest_is_written_after_a_week()
    {
        var before = Clock.UtcNow;
        try
        {
            var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
            var pid = await ProductAsync(admin, approver);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await approver.PostAsync($"/api/v1/products/{pid}/ops/check", null)).StatusCode);   // approvers cannot see this
            (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", new { enabled = true, channel = "facebook", goal = "Introduce the product", planDays = 3, everyDays = 3, postTimeUtc = "13:00", maxPostsPerDay = 2, lookaheadHours = 96, useImages = false, useVideos = false })).EnsureSuccessStatusCode();

            Script(PlanJson(), PostJson("Our pharmacy POS keeps working offline."), Approve1);
            (await admin.PostAsync($"/api/v1/products/{pid}/autopilot/run", null)).EnsureSuccessStatusCode();
            await CheckAsync(admin, pid);
            Assert.Equal(0, (await OpsAsync(admin, pid)).GetProperty("alerts").GetArrayLength());

            Clock.Advance(TimeSpan.FromHours(2));                                              // the loop stopped: nothing has run for two hours
            await CheckAsync(admin, pid);
            var alerts = (await OpsAsync(admin, pid)).GetProperty("alerts");
            Assert.Equal("autopilot-stalled", alerts.EnumerateArray().Single().GetProperty("key").GetString());
            Assert.Equal("critical", alerts[0].GetProperty("severity").GetString());
            await CheckAsync(admin, pid);                                                      // checking again does not duplicate it
            Assert.Equal(1, (await OpsAsync(admin, pid)).GetProperty("alerts").GetArrayLength());

            Script();
            (await admin.PostAsync($"/api/v1/products/{pid}/autopilot/run", null)).EnsureSuccessStatusCode();   // it runs again
            await CheckAsync(admin, pid);
            Assert.Equal(0, (await OpsAsync(admin, pid)).GetProperty("alerts").GetArrayLength());

            // a week later the digest is due; it is written once
            Clock.Advance(TimeSpan.FromDays(8));
            Script(); (await admin.PostAsync($"/api/v1/products/{pid}/autopilot/run", null)).EnsureSuccessStatusCode();
            Assert.True((await CheckAsync(admin, pid)).GetProperty("digestWritten").GetBoolean());
            Assert.False((await CheckAsync(admin, pid)).GetProperty("digestWritten").GetBoolean());
            var digests = (await OpsAsync(admin, pid)).GetProperty("digests");
            Assert.Equal(1, digests.GetArrayLength());
            Assert.Contains("Weekly growth digest", digests[0].GetProperty("subject").GetString());
            Assert.Contains("AI spend this month", digests[0].GetProperty("body").GetString());

            var manual = await admin.PostAsync($"/api/v1/products/{pid}/ops/digest", null);
            Assert.True(manual.IsSuccessStatusCode);
            Assert.Equal(2, (await OpsAsync(admin, pid)).GetProperty("digests").GetArrayLength());
        }
        finally { Clock.Set(before); }
    }

    [Fact]
    public async Task Nothing_is_checked_for_a_product_without_the_autopilot()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        await CheckAsync(admin, pid);
        Assert.Equal(0, (await OpsAsync(admin, pid)).GetProperty("alerts").GetArrayLength());
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/products/{pid}/ops/digest", null)).StatusCode);
    }
}
