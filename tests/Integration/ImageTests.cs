using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Ai;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>Stands in for the Stability model on Bedrock: records what it was asked and returns a PNG with a real header (1024 x 1024).</summary>
public sealed class FakeImageProvider : IImageProvider
{
    public static readonly List<ImageProviderRequest> Calls = [];
    public static string? FailWith;
    public string Name => "Bedrock";
    public bool IsConfigured => true;
    public string Model => "stability.stable-image-core-v1:1";

    public Task<ImageProviderResult> GenerateAsync(ImageProviderRequest r, CancellationToken ct)
    {
        lock (Calls) Calls.Add(r);
        if (FailWith == "refuse") throw new AiException("The image model declined this request (Filter reason: prompt).", AiOutcome.Refused);
        var png = new byte[40];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 4, 0, 0, 0, 4, 0 }.CopyTo(png, 0);
        return Task.FromResult(new ImageProviderResult(png, "image/png"));
    }
}

[Collection("host")]
public sealed class ImageTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private static int Calls { get { lock (FakeImageProvider.Calls) return FakeImageProvider.Calls.Count; } }

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"img-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private async Task<Guid> ProductAsync(HttpClient admin, bool approved = true)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Picture Game " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        var pid = (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        if (!approved) return pid;
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new
        {
            content = new
            {
                summary = "A friendly dice and board game for families",
                facts = new object[] { new { id = "f-1", category = "Feature", text = "Plays with up to four people", evidence = "Spec", verified = true } },
                claims = Array.Empty<object>(), markets = new[] { new { country = "PK", languages = new[] { "en" } } },
            },
        });
        (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null)).EnsureSuccessStatusCode();
        var approver = await UserAsync(admin, "Approver");
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={pid}", Json);
        var aid = list.EnumerateArray().Single().GetProperty("id").GetGuid();
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{aid}/decision", new { decision = "Approved" })).EnsureSuccessStatusCode();
        return pid;
    }

    private static Task<HttpResponseMessage> Make(HttpClient c, Guid pid, string brief, string shape = "1:1")
        => c.PostAsJsonAsync($"/api/v1/products/{pid}/ai/images", new { brief, aspectRatio = shape });

    [Fact]
    public async Task A_generated_picture_is_filed_as_a_labelled_synthetic_asset_and_its_cost_is_recorded()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var before = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/usage", Json)).GetProperty("monthToDateUsd").GetDecimal();

        var res = await Make(admin, pid, "A colourful board game on a sunny balcony", "9:16");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0.04m, body.GetProperty("costUsd").GetDecimal());

        var asset = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/assets/{body.GetProperty("assetId").GetGuid()}", Json)).GetProperty("asset");
        Assert.True(asset.GetProperty("isSynthetic").GetBoolean());
        Assert.Equal("AiGenerated", asset.GetProperty("source").GetString());
        Assert.Equal("image/png", asset.GetProperty("contentType").GetString());
        Assert.Equal(1024, asset.GetProperty("width").GetInt32());
        Assert.Contains("AI-generated", asset.GetProperty("rightsNotes").GetString());
        Assert.Contains("colourful board", asset.GetProperty("rightsNotes").GetString());   // the brief travels with the file

        var after = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/usage", Json)).GetProperty("monthToDateUsd").GetDecimal();
        Assert.Equal(0.04m, after - before);

        // what the model was told: the product, its approved summary, the no-text rules, and the shape asked for
        ImageProviderRequest sent; lock (FakeImageProvider.Calls) sent = FakeImageProvider.Calls[^1];
        Assert.Contains("Picture Game", sent.Prompt);
        Assert.Contains("friendly dice and board game", sent.Prompt);
        Assert.Contains("no text", sent.Prompt);
        Assert.Contains("screenshot", sent.NegativePrompt);
        Assert.Equal("9:16", sent.AspectRatio);
    }

    [Theory]
    [InlineData("A screenshot of the game board")]
    [InlineData("Show the in-game leaderboard and level screen")]
    [InlineData("A poster with the text WIN BIG")]
    [InlineData("The Google Play badge next to a phone")]
    [InlineData("A portrait of a famous cricketer holding the dice")]
    public async Task A_brief_that_asks_for_something_fake_is_refused_before_the_model_is_called_or_anything_is_charged(string brief)
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var calls = Calls;
        var res = await Make(admin, pid, brief);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("image.refused", (await res.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
        Assert.Equal(calls, Calls);
    }

    [Fact]
    public async Task Bad_input_and_a_product_without_an_approved_profile_never_reach_the_model()
    {
        var admin = await host.LoginAdminAsync(); var calls = Calls;
        var pid = await ProductAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await Make(admin, pid, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Make(admin, pid, new string('a', 501))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Make(admin, pid, "A sunny park", "7:3")).StatusCode);
        var bare = await ProductAsync(admin, approved: false);
        Assert.Equal(HttpStatusCode.Conflict, (await Make(admin, bare, "A sunny park")).StatusCode);
        Assert.Equal(calls, Calls);
    }

    [Fact]
    public async Task A_refusal_by_the_image_model_costs_nothing_and_is_reported_as_declined()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var before = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/usage", Json)).GetProperty("monthToDateUsd").GetDecimal();
        FakeImageProvider.FailWith = "refuse";
        try { Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Make(admin, pid, "A sunny park")).StatusCode); }
        finally { FakeImageProvider.FailWith = null; }
        Assert.Equal(before, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/usage", Json)).GetProperty("monthToDateUsd").GetDecimal());
    }

    [Fact]
    public async Task A_product_stops_at_its_monthly_picture_limit()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        for (var i = 0; i < 40; i++) Assert.Equal(HttpStatusCode.OK, (await Make(admin, pid, "A sunny park " + i)).StatusCode);
        var calls = Calls;
        var over = await Make(admin, pid, "One more sunny park");
        Assert.Equal(HttpStatusCode.PaymentRequired, over.StatusCode);
        Assert.Equal(calls, Calls);   // blocked before the model was called
    }

    [Fact]
    public async Task A_user_without_the_ai_permission_cannot_make_pictures()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var viewer = await UserAsync(admin, "Read-only Viewer");
        var calls = Calls;
        Assert.Equal(HttpStatusCode.NotFound, (await Make(viewer, pid, "A sunny park")).StatusCode);
        Assert.Equal(calls, Calls);
        var creator = await UserAsync(admin, "Content Creator");   // may generate, though not upload
        Assert.Equal(HttpStatusCode.OK, (await Make(creator, pid, "A sunny park")).StatusCode);
    }

    [Fact]
    public async Task A_generated_picture_can_be_attached_to_a_post_like_any_other_file()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var assetId = (await (await Make(admin, pid, "A sunny park")).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("assetId").GetGuid();
        var created = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "AI picture post", channel = "facebook", format = "Post", body = "Game night!", assetIds = new[] { assetId } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
