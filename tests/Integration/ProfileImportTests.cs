using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TriSoftic.GrowthEngine.Modules.Portfolio;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>Filling a profile from a web page: the AI proposes, the page must back every entry, nothing is saved or verified without a person.</summary>
[Collection("host")]
public sealed class ProfileImportTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private const string PageText = "Ludo Fight is a multiplayer Ludo game for Android. Play with up to 4 players online. Chat with friends during the match. Available in English and Urdu for players in Pakistan.";

    private async Task<(HttpClient Admin, Guid Pid)> ProductAsync()
    {
        var admin = await host.LoginAdminAsync();
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Android Game").GetProperty("id").GetGuid();
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Imp " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        return (admin, pid);
    }

    private static void Page() => FakePageFetcher.Page = new FetchedPage("https://example.com/ludo", "Ludo Fight", PageText);
    private static void Script(params string[] r) { FakeAiProvider.Script.Clear(); foreach (var x in r) FakeAiProvider.Script.Enqueue(x); FakeAiProvider.OutputTokens = 500; FakeAiProvider.Prompts.Clear(); }
    private const string GoodAnswer = """
        {"summary":"A multiplayer Ludo game for Android.","facts":[
          {"category":"Feature","text":"Supports up to 4 players online","quote":"Play with up to 4 players online"},
          {"category":"Feature","text":"Has daily tournaments","quote":"Join daily tournaments and win prizes"}],
         "markets":[{"country":"PK","languages":["en","ur"],"quote":"Available in English and Urdu for players in Pakistan"}],
         "platforms":["Android"],"tone":"Friendly"}
        """;

    [Fact]
    public async Task A_page_is_turned_into_a_proposed_profile_with_unverified_facts_and_nothing_is_saved()
    {
        var (admin, pid) = await ProductAsync();
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        Page(); Script(GoodAnswer);
        var r = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/profile/import-url", new { url = "https://example.com/ludo", current = new { summary = "", facts = Array.Empty<object>() } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        var profile = body.GetProperty("profile");
        Assert.Equal("A multiplayer Ludo game for Android.", profile.GetProperty("summary").GetString());
        var fact = profile.GetProperty("facts").EnumerateArray().Single();
        Assert.Equal("Supports up to 4 players online", fact.GetProperty("text").GetString());
        Assert.False(fact.GetProperty("verified").GetBoolean());
        Assert.Contains("https://example.com/ludo", fact.GetProperty("evidence").GetString());
        Assert.Equal(1, body.GetProperty("dropped").GetInt32());                       // the tournament fact is not on the page
        Assert.Equal("PK", profile.GetProperty("markets")[0].GetProperty("country").GetString());
        Assert.Contains("https://example.com/ludo", FakePageFetcher.Requested);
        Assert.Contains("<<<PAGE", FakeAiProvider.Prompts[0]);

        // the draft on the server is untouched until a person saves it
        var v = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/profile/versions/1", Json);
        Assert.Equal(0, v.GetProperty("content").GetProperty("facts").GetArrayLength());
    }

    [Fact]
    public async Task Bad_addresses_unreadable_pages_and_unusable_answers_are_refused_with_a_reason()
    {
        var (admin, pid) = await ProductAsync();
        Page();
        foreach (var bad in new[] { "http://169.254.169.254/latest/meta-data/", "http://localhost:5000/", "file:///etc/passwd", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/profile/import-url", new { url = bad })).StatusCode);

        FakePageFetcher.FailWith = "The page could not be reached.";
        try
        {
            var r = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/profile/import-url", new { url = "https://example.com/down" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Contains("could not be reached", await r.Content.ReadAsStringAsync());
        }
        finally { FakePageFetcher.FailWith = null; }

        Script("I cannot help with that.", "Still no JSON");
        var junk = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/profile/import-url", new { url = "https://example.com/ludo" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, junk.StatusCode);
    }

    [Fact]
    public async Task Only_people_who_can_edit_profiles_and_use_the_AI_may_import()
    {
        var (admin, pid) = await ProductAsync();
        var email = $"imp-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Approver", password = "Some-Strong-Pass-123", roles = new[] { "Approver" }, allProducts = true })).EnsureSuccessStatusCode();
        var approver = await host.LoginAsync(email, "Some-Strong-Pass-123");
        Page(); Script(GoodAnswer);
        Assert.Equal(HttpStatusCode.NotFound, (await approver.PostAsJsonAsync($"/api/v1/products/{pid}/profile/import-url", new { url = "https://example.com/ludo" })).StatusCode);
    }
}
