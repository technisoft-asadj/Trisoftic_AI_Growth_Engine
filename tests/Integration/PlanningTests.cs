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
    /// <summary>Letters only, so an angle never contains a number the planner could be accused of inventing.</summary>
    private static string Letters() => new(Uniq().Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
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

    private static string PlanJson(int n, string rationale = "A short run that introduces the product, then shows how it works offline.") => JsonSerializer.Serialize(new
    {
        rationale,
        measures = new[] { new { name = "Reactions and comments", why = "shows interest" }, new { name = "Link clicks", why = "shows intent" } },
        slots = Enumerable.Range(1, n).Select(i => new { angle = $"Angle {i} " + Letters(), brief = "Cover a verified fact.", hookType = HookTypes.All[(i - 1) % HookTypes.All.Length] }),
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
    public async Task Hook_type_tags_flow_into_a_leaderboard_and_the_planner_may_only_quote_figures_from_it()
    {
        var before = Clock.UtcNow;
        try
        {
            var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
            var pid = await ProductAsync(admin, approver);

            // a plan's slots carry hook types, and the drafted post carries its slot's tag
            Script(PlanJson(3));
            var planId = (await (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", NewPlan(Clock.UtcNow))).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
            var slots = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("slots").EnumerateArray().ToList();
            Assert.Equal(new[] { "Question", "Challenge", "Scenario" }, slots.Select(s => s.GetProperty("hookType").GetString()).ToArray());
            Script(Post, Post, Post);
            foreach (var s in slots) (await admin.PostAsync($"/api/v1/plans/{planId}/slots/{s.GetProperty("id").GetGuid()}/draft", null)).EnsureSuccessStatusCode();
            var itemIds = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}", Json)).GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("contentItemId").GetGuid()).ToList();
            Assert.Equal("Question", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{itemIds[0]}", Json)).GetProperty("item").GetProperty("hookType").GetString());

            // a tag can be corrected, but only to a name from the fixed list
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/content/{itemIds[2]}/hook-type", new { hookType = "Clickbait" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/content/{itemIds[2]}/hook-type", new { hookType = "Tip" })).StatusCode);

            // before anything is published the leaderboard is empty and the planner is given none
            var empty = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/leaderboard", Json);
            Assert.Equal(0, empty.GetProperty("published").GetInt32());

            foreach (var id in itemIds) await Approve(admin, approver, id.ToString());
            (await admin.PostAsync($"/api/v1/plans/{planId}/schedule", null)).EnsureSuccessStatusCode();
            Clock.Advance(TimeSpan.FromDays(6));
            await RunJobs("publishing.dispatch");
            ScriptedPublisher.PostFigures = new(Reactions: 12, Comments: 3, Shares: 0, Views: null, Reach: null, Clicks: null, Note: null);
            try { await RunJobs("publishing.pull-post-metrics"); } finally { ScriptedPublisher.PostFigures = null; }

            var lb = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/leaderboard", Json);
            Assert.Equal(3, lb.GetProperty("published").GetInt32()); Assert.Equal(3, lb.GetProperty("measured").GetInt32());
            JsonElement Cell(string dim, string key) => lb.GetProperty("dimensions").EnumerateArray().Single(d => d.GetProperty("name").GetString() == dim).GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);
            var post = Cell("Format", "Post");
            Assert.Equal(3, post.GetProperty("posts").GetInt32()); Assert.Equal(15m, post.GetProperty("avgEngagements").GetDecimal());
            Assert.True(post.GetProperty("tooFew").GetBoolean());                                        // 3 measured posts is under the minimum of 5
            Assert.Equal(JsonValueKind.Null, post.GetProperty("avgViews").ValueKind);                    // views were not reported: null, never 0
            Assert.Equal(1, Cell("Hook type", "Tip").GetProperty("posts").GetInt32());                   // the corrected tag, not the planned one

            // the planner is given the table and must quote only figures from it; an invented figure sends it back once
            Script(PlanJson(3, "Tip posts averaged 99 engagements, so use more of them."), PlanJson(3, "Posts so far averaged 15 engagements, but 3 posts is too few to rely on."));
            FakeAiProvider.Prompts.Clear();
            var next = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", NewPlan(Clock.UtcNow));
            Assert.Equal(HttpStatusCode.Created, next.StatusCode);
            Assert.Equal(2, FakeAiProvider.Prompts.Count);
            Assert.Contains("LEADERBOARD", FakeAiProvider.Prompts[0]); Assert.Contains("TOO FEW", FakeAiProvider.Prompts[0]);
            Assert.Contains("not in the leaderboard", FakeAiProvider.Prompts[1], StringComparison.OrdinalIgnoreCase);

            // a planner that keeps inventing figures is refused, and no plan is saved
            Script(PlanJson(3, "Tips averaged 99 engagements."), PlanJson(3, "Tips averaged 99 engagements."));
            var refused = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/plans", NewPlan(Clock.UtcNow));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("99", await refused.Content.ReadAsStringAsync());
        }
        finally { Clock.Set(before); }
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
