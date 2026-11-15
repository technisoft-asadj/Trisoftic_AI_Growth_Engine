using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Ai;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>Scripted provider standing in for Claude/OpenAI in tests (no network, no keys).</summary>
public sealed class FakeAiProvider : IAiProvider
{
    public static readonly ConcurrentQueue<string> Script = new();
    public static int OutputTokens = 500;
    public static readonly List<string> Prompts = [];
    public string Name => "Anthropic";
    public bool IsConfigured => true;
    public string? ModelFor(AiTier tier) => tier == AiTier.Strategy ? "claude-opus-5-5" : "claude-sonnet-5-5";

    public Task<ProviderResponse> CompleteAsync(string model, string system, string user, int maxTokens, CancellationToken ct)
    {
        lock (Prompts) Prompts.Add(system + "\n---\n" + user);
        if (!Script.TryDequeue(out var text)) throw new AiException("fake provider script exhausted", AiOutcome.Failed);
        if (text == "REFUSE") throw new AiException("declined", AiOutcome.Refused);
        return Task.FromResult(new ProviderResponse(text, 1000, OutputTokens));
    }
}

[Collection("host")]
public sealed class AiTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Good = """{"title":"Offline","body":"Pharmacy POS works offline and syncs when online.","claims":[{"text":"Works offline","factRef":"f-offline","assumption":false}],"questions":[]}""";
    private const string InventedFigure = """{"title":"x","body":"Trusted by 5,000 pharmacies.","claims":[{"text":"Works offline","factRef":"f-offline","assumption":false}],"questions":[]}""";
    private const string Prohibited = """{"title":"x","body":"Our POS is FDA approved.","claims":[{"text":"Works offline","factRef":"f-offline","assumption":false}],"questions":[]}""";

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"{role.Replace(' ', '-').ToLowerInvariant()}-{Guid.NewGuid():N}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private async Task<Guid> ProductWithApprovedProfileAsync(HttpClient admin, bool approve = true)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Pharmacy POS").GetProperty("id").GetGuid();
        var created = await admin.PostAsJsonAsync("/api/v1/products", new { name = "AI Pharmacy " + Guid.NewGuid().ToString("N")[..6], categoryId = catId });
        var pid = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        if (!approve) return pid;

        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new
        {
            content = new
            {
                summary = "Point of sale for pharmacies",
                facts = new object[]
                {
                    new { id = "f-offline", category = "Feature", text = "Works offline", evidence = "Spec", verified = true },
                    new { id = "f-unverified", category = "Feature", text = "Has drone delivery", evidence = (string?)null, verified = false },
                },
                claims = new object[] { new { id = "c-fda", text = "FDA approved", status = "Prohibited" } },
                markets = new[] { new { country = "PK", languages = new[] { "en", "ur" } } },
            },
        });
        (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null)).EnsureSuccessStatusCode();
        var approver = await UserAsync(admin, "Approver");
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={pid}", Json);
        var aid = list.EnumerateArray().Single().GetProperty("id").GetGuid();
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{aid}/decision", new { decision = "Approved" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static void Script(params string[] responses) { FakeAiProvider.Script.Clear(); foreach (var r in responses) FakeAiProvider.Script.Enqueue(r); FakeAiProvider.OutputTokens = 500; }

    private static Task<HttpResponseMessage> Generate(HttpClient c, Guid pid, string brief = "Announce offline mode", string? language = null, string kind = "ContentDraft") =>
        c.PostAsJsonAsync($"/api/v1/products/{pid}/ai/generate", new { kind, brief, channel = "Facebook post", language });

    [Fact]
    public async Task Cannot_generate_without_an_approved_profile()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin, approve: false);
        Script(Good);
        Assert.Equal(HttpStatusCode.Conflict, (await Generate(admin, pid)).StatusCode);
        Assert.Single(FakeAiProvider.Script);   // the model was never called
    }

    [Fact]
    public async Task Grounded_generation_succeeds_records_usage_and_only_sends_verified_facts()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin);
        FakeAiProvider.Prompts.Clear(); Script(Good);

        var res = await Generate(admin, pid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(1, body.GetProperty("attempts").GetInt32());
        Assert.Equal(1, body.GetProperty("profileVersion").GetInt32());

        var prompt = FakeAiProvider.Prompts.Single();
        Assert.Contains("f-offline | Feature | Works offline", prompt);
        Assert.DoesNotContain("drone delivery", prompt);          // unverified fact never reaches the model
        Assert.Contains("FDA approved", prompt);                   // listed only as PROHIBITED
        Assert.Contains("PROHIBITED CLAIMS", prompt);

        var usage = await admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/usage", Json);
        Assert.Contains(usage.GetProperty("products").EnumerateArray(), p => p.GetProperty("productId").ValueKind != JsonValueKind.Null && p.GetProperty("productId").GetGuid() == pid && p.GetProperty("calls").GetInt32() == 1);
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={pid}", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "ai.generated");
    }

    [Fact]
    public async Task Invented_statistic_is_repaired_on_second_attempt()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin);
        FakeAiProvider.Prompts.Clear(); Script(InventedFigure, Good);

        var res = await Generate(admin, pid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("attempts").GetInt32());
        Assert.Contains("broke these rules", FakeAiProvider.Prompts[1]);
        Assert.Contains("5,000", FakeAiProvider.Prompts[1]);
    }

    [Fact]
    public async Task Persistent_violations_are_never_returned_to_the_user()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin);
        Script(Prohibited, Prohibited);
        var res = await Generate(admin, pid);
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Contains("prohibited", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unverified_language_is_rejected_before_calling_the_model()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin);
        Script(Good);
        Assert.Equal(HttpStatusCode.BadRequest, (await Generate(admin, pid, language: "fr")).StatusCode);
        Assert.Single(FakeAiProvider.Script);
        Script(Good);
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin, pid, language: "ur")).StatusCode);
    }

    [Fact]
    public async Task Refusal_is_surfaced_not_retried_on_another_model()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin);
        Script("REFUSE", Good);
        Assert.Equal((HttpStatusCode)422, (await Generate(admin, pid)).StatusCode);
        Assert.Single(FakeAiProvider.Script);   // fallback response untouched
    }

    [Fact]
    public async Task Product_budget_blocks_further_calls_and_pause_blocks_everything()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin);
        Script(Good, Good);
        FakeAiProvider.OutputTokens = 1_100_000;   // Draft tier (sonnet-5-5 @ $10/MTok out) => ~$11 per call; cap is $20
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin, pid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin, pid)).StatusCode);
        Script(Good);
        Assert.Equal((HttpStatusCode)402, (await Generate(admin, pid)).StatusCode);   // $22 spent >= $20

        var other = await ProductWithApprovedProfileAsync(admin);
        await admin.PutAsJsonAsync("/api/v1/governance/pause/Ai", new { isPaused = true, reason = "test" });
        try { Script(Good); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Generate(admin, other)).StatusCode); }
        finally { await admin.PutAsJsonAsync("/api/v1/governance/pause/Ai", new { isPaused = false }); FakeAiProvider.OutputTokens = 500; }
    }

    [Fact]
    public async Task Permissions_are_enforced()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductWithApprovedProfileAsync(admin);
        var viewer = await UserAsync(admin, "Read-only Viewer");
        Script(Good);
        Assert.Equal(HttpStatusCode.NotFound, (await Generate(viewer, pid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/ai/usage")).StatusCode);        // Viewer holds budget.read
        var creator = await UserAsync(admin, "Content Creator");
        Assert.Equal(HttpStatusCode.OK, (await Generate(creator, pid)).StatusCode);                     // Creator holds ai.generate
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.GetAsync("/api/v1/ai/usage")).StatusCode); // ...but not budget.read
    }
}
