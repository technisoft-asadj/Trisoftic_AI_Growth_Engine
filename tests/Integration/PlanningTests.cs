using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>The whole loop: plan -> draft -> approve -> schedule -> publish -> results -> review -> next plan, with a scripted model and a scripted network.</summary>
[Collection("host")]
public sealed class PlanningTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"plan-{Uniq()}@test.local";
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
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Plan " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
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
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Plan page" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static string PlanJson(int n) => JsonSerializer.Serialize(new
    {
        rationale = "A short run that introduces the product, then shows how it works offline.",
        measures = new[] { new { name = "Reactions and comments", why = "shows interest" }, new { name = "Link clicks", why = "shows intent" } },
        slots = Enumerable.Range(1, n).Select(i => new { angle = $"Angle {i} " + Uniq(), brief = "Cover a verified fact." }),
    });

    private const string Post = """{"title":"Offline","body":"Our pharmacy POS works offline.","claims":[{"text":"Works offline","factRef":"f-offline","assumption":false}],"questions":[]}""";
    private const string PostWithPlaceholder = """{"title":"Offline","body":"Our pharmacy POS works offline. Get it at [store link]","claims":[{"text":"Works offline","factRef":"f-offline","assumption":false}],"questions":[]}""";

    private static void Script(params string[] responses) { FakeAiProvider.Script.Clear(); foreach (var r in responses) FakeAiProvider.Script.Enqueue(r); FakeAiProvider.OutputTokens = 500; }

    private async Task RunJobs(string type)
    {
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest(type, "{}", IdempotencyKey: type + Guid.NewGuid()));
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var k = 0; k < 200 && await runner.RunOnceAsync(default); k++) { }
    }

    private static object NewPlan(DateTimeOffset now, int days = 3, Guid? fromReview = null) => new { name = "Launch run", goal = "Introduce the product to new pharmacists", startDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1), days, everyDays = 1, timeUtc = "23:50", channel = "facebook", language = "en", fromReviewId = fromReview };

    [Fact]
    public async Task A_plan_goes_from_idea_to_published_posts_to_an_honest_review_to_a_next_plan()
    {
        var before = Clock.UtcNow;
        try { await WholeLoopAsync(); }
        finally { Clock.Set(before); }   // the clock is shared with every other test; leave it where it was found
    }

    private async Task WholeLoopAsync()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);

        // ---- plan: slots and dates come from the cadence, angles from the model ----
        Script(PlanJson(3));
        var created = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", NewPlan(Clock.UtcNow));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var planId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var plan = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json);
        Assert.Equal("Draft", plan.GetProperty("status").GetString());
        var slots = plan.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(3, slots.Count);
        Assert.All(slots, s => Assert.Equal("Planned", s.GetProperty("status").GetString()));
        Assert.Equal(new[] { 0, 1, 2 }, slots.Select(s => (s.GetProperty("scheduledFor").GetDateTimeOffset().Date - slots[0].GetProperty("scheduledFor").GetDateTimeOffset().Date).Days));

        // ---- draft: a placeholder is refused and leaves the slot free to retry ----
        var slotIds = slots.Select(s => s.GetProperty("id").GetGuid()).ToList();
        Script(PostWithPlaceholder);
        var bad = await admin.PostAsync($"/api/v1/plans/{planId}/slots/{slotIds[0]}/draft", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode); Assert.Contains("placeholder", await bad.Content.ReadAsStringAsync());
        Script(Post, Post, Post);
        foreach (var sid in slotIds) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/plans/{planId}/slots/{sid}/draft", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/plans/{planId}/slots/{slotIds[0]}/draft", null)).StatusCode);   // already drafted
        plan = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json);
        var itemIds = plan.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("contentItemId").GetGuid()).ToList();
        Assert.All(plan.GetProperty("slots").EnumerateArray(), s => { Assert.Equal("Drafted", s.GetProperty("status").GetString()); Assert.Equal("InReview", s.GetProperty("content").GetProperty("status").GetString()); });

        // ---- schedule: nothing goes out until each post is approved ----
        var early = await (await admin.PostAsync($"/api/v1/plans/{planId}/schedule", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, early.GetProperty("scheduled").GetInt32()); Assert.Equal(3, early.GetProperty("skipped").GetArrayLength());
        foreach (var id in itemIds) await Approve(admin, approver, id.ToString());
        var sched = await (await admin.PostAsync($"/api/v1/plans/{planId}/schedule", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(3, sched.GetProperty("scheduled").GetInt32());
        Assert.Equal("Active", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("status").GetString());

        // ---- review with nothing published: honest, and the model is never called ----
        Script();
        var r0 = await admin.PostAsync($"/api/v1/plans/{planId}/review", null);
        Assert.Equal(HttpStatusCode.Created, r0.StatusCode);
        var first = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("reviews")[0];
        Assert.Equal("low", first.GetProperty("result").GetProperty("confidence").GetString());
        Assert.Contains("None of this plan's posts", first.GetProperty("result").GetProperty("caveats")[0].GetString());

        // ---- publish all three, then pull figures ----
        Clock.Advance(TimeSpan.FromDays(6));
        await RunJobs("publishing.dispatch");
        plan = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json);
        Assert.All(plan.GetProperty("slots").EnumerateArray(), s => Assert.Equal("Published", s.GetProperty("content").GetProperty("status").GetString()));

        ScriptedPublisher.PostFigures = null;
        await RunJobs("publishing.pull-post-metrics");
        Script();
        await admin.PostAsync($"/api/v1/plans/{planId}/review", null);   // published but nothing reported yet: still no model call, and it says why
        var waiting = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("reviews")[0];
        Assert.Contains("3 post(s) are published", waiting.GetProperty("result").GetProperty("caveats")[0].GetString());

        ScriptedPublisher.PostFigures = new(Reactions: 12, Comments: 3, Shares: 0, Views: null, Reach: null, Clicks: null, Note: null);
        try { await RunJobs("publishing.pull-post-metrics"); } finally { ScriptedPublisher.PostFigures = null; }

        // a review that quotes a figure it was not given is sent back once, then accepted
        const string Invented = """{"headline":"Post 1 reached 5,000 people","confidence":"low","whatWorked":[],"whatDidNot":[],"learnings":[],"nextPlan":{"goal":"g","suggestedAngles":[]},"caveats":[]}""";
        const string Honest = """{"headline":"Each post drew 15 engagements","confidence":"low","whatWorked":["Posts 1 to 3 each had 12 reactions"],"whatDidNot":[],"learnings":["Views were not reported, so reach cannot be judged yet"],"nextPlan":{"goal":"Test a question-style post","suggestedAngles":[{"angle":"Ask followers a question","why":"Comments were the smallest figure, so test a prompt that invites them"}]},"caveats":["Only 3 posts; treat as a hint, not proof"]}""";
        Script(Invented, Honest);
        FakeAiProvider.Prompts.Clear();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"/api/v1/plans/{planId}/review", null)).StatusCode);
        Assert.Equal(2, FakeAiProvider.Prompts.Count);
        Assert.Contains("not reported", FakeAiProvider.Prompts[0]);                        // missing views are shown as missing, not zero
        Assert.Contains("these figures were not in the data", FakeAiProvider.Prompts[1], StringComparison.OrdinalIgnoreCase);
        var review = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("reviews")[0];
        Assert.Equal(3, review.GetProperty("dataQuality").GetProperty("measured").GetInt32());
        Assert.Equal("Each post drew 15 engagements", review.GetProperty("result").GetProperty("headline").GetString());
        var reviewId = review.GetProperty("id").GetGuid();
        Assert.All((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("slots").EnumerateArray(), s =>
        { Assert.Equal(12, s.GetProperty("results").GetProperty("reactions").GetInt32()); Assert.Equal(JsonValueKind.Null, s.GetProperty("results").GetProperty("views").ValueKind); });

        // ---- next plan carries the learnings ----
        Script(PlanJson(3)); FakeAiProvider.Prompts.Clear();
        var next = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", NewPlan(Clock.UtcNow, fromReview: reviewId));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        var prompt = FakeAiProvider.Prompts.Single();
        Assert.Contains("WHAT THE PREVIOUS PLAN TAUGHT US", prompt); Assert.Contains("Ask followers a question", prompt); Assert.Contains("Only 3 posts; treat as a hint, not proof", prompt);
        var nextId = (await next.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.Equal(planId, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{nextId}", Json)).GetProperty("previousPlanId").GetGuid());
    }

    [Fact]
    public async Task Planning_refuses_bad_input_and_people_without_permission_and_cannot_start_without_an_approved_profile()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin, approver);
        var viewer = await UserAsync(admin, "Read-only Viewer");
        Script(PlanJson(3));
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/products/{pid}/plans", NewPlan(Clock.UtcNow))).StatusCode);
        Assert.Single(FakeAiProvider.Script);   // the model was never called

        var tooBig = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", new { name = "x", goal = "y", startDate = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime), days = 60, everyDays = 1, channel = "facebook" });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        var badLang = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", new { name = "x", goal = "y", startDate = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime), days = 3, channel = "facebook", language = "fr" });
        Assert.Equal(HttpStatusCode.BadRequest, badLang.StatusCode);
        Assert.Single(FakeAiProvider.Script);

        // a planner that returns the wrong number of posts twice is not trusted
        Script(PlanJson(2), PlanJson(2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", NewPlan(Clock.UtcNow))).StatusCode);

        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var bare = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "NoProfile " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Script(PlanJson(3));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/products/{bare}/plans", NewPlan(Clock.UtcNow))).StatusCode);
        Assert.Single(FakeAiProvider.Script);
    }
}
