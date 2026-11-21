using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>The learnings register: reviews add hypotheses, later plans must test them, and the autopilot keeps it moving with no one pressing anything.</summary>
[Collection("host")]
public sealed class LearningTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private static string Letters() => new(Uniq().Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"lr-{Uniq()}@test.local";
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
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Lrn " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
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
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Lrn page" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static void Script(params string[] r) { FakeAiProvider.Script.Clear(); foreach (var x in r) FakeAiProvider.Script.Enqueue(x); FakeAiProvider.OutputTokens = 500; FakeAiProvider.Prompts.Clear(); }
    private static string PlanJson(string? tests = null) => JsonSerializer.Serialize(new
    {
        rationale = "A short run.", measures = new[] { new { name = "Reactions", why = "interest" } },
        slots = new[] { new { angle = "Angle " + Letters(), brief = "Cover a verified fact.", hookType = "Question", tests } },
    });
    private static string PostJson(string body) => JsonSerializer.Serialize(new { title = "Offline", body, claims = new[] { new { text = "Works offline", factRef = "f-offline", assumption = false } }, questions = Array.Empty<string>() });
    private const string Approve1 = """{"verdict":"approve","issues":[],"summary":"Grounded and in voice."}""";

    private async Task RunJobs(string type)
    {
        using (var scope = host.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest(type, "{}", IdempotencyKey: type + Guid.NewGuid()));
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var k = 0; k < 200 && await runner.RunOnceAsync(default); k++) { }
    }

    private static async Task<string> RunAsync(HttpClient c, Guid pid)
    {
        var r = await c.PostAsync($"/api/v1/products/{pid}/autopilot/run", null);
        var body = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, body);
        return string.Join(" ", JsonSerializer.Deserialize<JsonElement>(body, Json).GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    private static Task<JsonElement> LearningsAsync(HttpClient c, Guid pid) => c.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/learnings", Json);

    [Fact]
    public async Task A_review_adds_a_hypothesis_the_next_plan_must_test_it_and_nobody_presses_anything()
    {
        var before = Clock.UtcNow;
        try
        {
            var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
            var pid = await ProductAsync(admin, approver);
            (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/autopilot", new { enabled = true, channel = "facebook", goal = "Introduce the product", planDays = 3, everyDays = 3, postTimeUtc = "13:00", maxPostsPerDay = 2, lookaheadHours = 96, useImages = false, useVideos = false })).EnsureSuccessStatusCode();

            Script(PlanJson(), PostJson("Our pharmacy POS keeps working offline."), Approve1);
            await RunAsync(admin, pid);                                                       // plan 1 is written, approved and scheduled
            Clock.Advance(TimeSpan.FromDays(6));
            await RunJobs("publishing.dispatch");
            ScriptedPublisher.PostFigures = new(Reactions: 8, Comments: 2, Shares: 1, Views: null, Reach: null, Clicks: null, Note: null);
            try { await RunJobs("publishing.pull-post-metrics"); } finally { ScriptedPublisher.PostFigures = null; }

            // the plan has ended: the autopilot reviews it by itself, and the review leaves a hypothesis in the register
            Script("""{"headline":"The post drew 11 engagements","confidence":"low","whatWorked":[],"whatDidNot":[],"learnings":["One post is too little to conclude"],"nextPlan":{"goal":"Test openings","suggestedAngles":[]},"caveats":["Only one post"],"hypotheses":[{"statement":"Question openings draw more comments than announcements","howToTest":"Alternate a question post and an announcement post"}],"updates":[]}""");
            Assert.Contains("Reviewed the plan", await RunAsync(admin, pid));
            var reg = await LearningsAsync(admin, pid);
            Assert.Equal(1, reg.GetArrayLength());
            Assert.Equal("Open", reg[0].GetProperty("status").GetString());
            var hid = reg[0].GetProperty("id").GetGuid();
            Assert.True(reg[0].GetProperty("fromReview").GetBoolean());

            // the next plan is told about it and is sent back until it tests it
            Script(PlanJson(), PlanJson(hid.ToString()), PostJson("Closing time rush? The POS keeps up even when the line drops."), Approve1);
            Assert.Contains("Planned 1 posts", await RunAsync(admin, pid));
            Assert.Contains("OPEN HYPOTHESES", FakeAiProvider.Prompts[0]); Assert.Contains(hid.ToString(), FakeAiProvider.Prompts[0]);
            Assert.Contains("Test at least 1", FakeAiProvider.Prompts[1]);
            var planId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/plans", Json)).EnumerateArray().First().GetProperty("id").GetGuid();
            var plan = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json);
            Assert.Equal(hid, plan.GetProperty("slots")[0].GetProperty("hypothesisId").GetGuid());
            Assert.Equal(1, (await LearningsAsync(admin, pid))[0].GetProperty("timesTested").GetInt32());
        }
        finally { Clock.Set(before); }
    }

    [Fact]
    public async Task A_person_can_add_and_retire_hypotheses_and_the_register_has_a_limit()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        Assert.Equal(HttpStatusCode.NotFound, (await approver.PostAsJsonAsync($"/api/v1/products/{pid}/learnings", new { statement = "Short videos get more reach than text" })).StatusCode);   // approvers cannot plan
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/learnings", new { statement = "no" })).StatusCode);
        var created = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/learnings", new { statement = "Short videos get more reach than text", howToTest = "Post a Reel and a text post in turn" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/learnings/{id}/retire", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/learnings/{id}/retire", null)).StatusCode);
        Assert.Equal("Retired", (await LearningsAsync(admin, pid))[0].GetProperty("status").GetString());

        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/learnings", new { statement = $"Hypothesis number {i} {Letters()}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/learnings", new { statement = "One hypothesis too many to keep open" })).StatusCode);
    }
}
