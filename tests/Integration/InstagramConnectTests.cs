using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>Connecting an Instagram professional account through its Facebook Page, and the rules that keep text-only posts away from Instagram.</summary>
[Collection("host")]
public sealed class InstagramConnectTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Ig " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> PendingAsync(HttpClient admin, Guid pid, string startQuery)
    {
        var start = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/connect/facebook/start?productId={pid}&channel=Instagram{startQuery}", Json);
        var url = new Uri(start.GetProperty("url").GetString()!);
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"]!;
        var ok = await host.NewClient().GetAsync($"/api/v1/publishing/connect/facebook/callback?code=abc&state={Uri.EscapeDataString(state)}");
        var loc = ok.Headers.Location!.OriginalString;
        return Guid.Parse(System.Web.HttpUtility.ParseQueryString(loc[(loc.IndexOf('?') + 1)..])["pending"]!);
    }

    [Fact]
    public async Task The_login_asks_for_Instagram_permissions_only_when_connecting_Instagram_and_the_account_is_found_through_the_Page()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);

        var plain = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/connect/facebook/start?productId={pid}&channel=Facebook", Json);
        Assert.DoesNotContain("instagram=1", plain.GetProperty("url").GetString());
        var ig = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/connect/facebook/start?productId={pid}&channel=Instagram&kind=instagram", Json);
        Assert.Contains("instagram=1", ig.GetProperty("url").GetString());

        var pending = await PendingAsync(admin, pid, "&kind=instagram");
        var sel = await admin.PostAsJsonAsync($"/api/v1/publishing/connect/pending/{pending}/select", new { pageId = "111" });
        Assert.Equal(HttpStatusCode.OK, sel.StatusCode);
        var raw = await sel.Content.ReadAsStringAsync();
        Assert.DoesNotContain("page-secret-token", raw);
        var account = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("instagram", account.GetProperty("provider").GetString());
        Assert.Equal("Instagram", account.GetProperty("channel").GetString());
        Assert.Equal("@ludofight_official", account.GetProperty("displayName").GetString());
        Assert.Equal("17841400000000001", account.GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task A_Page_without_a_linked_Instagram_professional_account_gets_a_clear_answer_and_nothing_is_connected()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var pending = await PendingAsync(admin, pid, "&kind=instagram");
        var before = FakeFacebookGraph.InstagramUsername;
        FakeFacebookGraph.InstagramUsername = null;
        try
        {
            var sel = await admin.PostAsJsonAsync($"/api/v1/publishing/connect/pending/{pending}/select", new { pageId = "111" });
            Assert.Equal(HttpStatusCode.Conflict, sel.StatusCode);
            Assert.Contains("Linked accounts", await sel.Content.ReadAsStringAsync());
            FakeFacebookGraph.Scopes = ["pages_show_list"];
            var again = await admin.PostAsJsonAsync($"/api/v1/publishing/connect/pending/{await PendingAsync(admin, pid, "&kind=instagram")}/select", new { pageId = "111" });
            var text = await again.Content.ReadAsStringAsync();
            Assert.Contains("instagram_basic and instagram_content_publish", text); Assert.Contains("which Instagram accounts", text);
            Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/accounts", Json)).GetArrayLength());
        }
        finally { FakeFacebookGraph.InstagramUsername = before; FakeFacebookGraph.Scopes = ["pages_show_list", "instagram_basic", "instagram_content_publish"]; }
    }

    [Fact]
    public async Task An_Instagram_post_without_a_picture_cannot_be_submitted_for_approval()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var created = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "Text only", channel = "Instagram", format = "Post", body = "Only words here." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var submit = await admin.PostAsync($"/api/v1/content/{id}/submit", null);
        Assert.False(submit.IsSuccessStatusCode);
        Assert.Contains("at least one picture", await submit.Content.ReadAsStringAsync());

        var facebook = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "Text only on Facebook", channel = "Facebook", format = "Post", body = "Only words here." });
        var fid = (await facebook.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.True((await admin.PostAsync($"/api/v1/content/{fid}/submit", null)).IsSuccessStatusCode);   // Facebook still takes text-only
    }

    private async Task<Guid> ConnectFacebookPageAsync(HttpClient admin, Guid pid, string pageId)
    {
        var pending = await PendingAsync(admin, pid, "");
        (await admin.PostAsJsonAsync($"/api/v1/publishing/connect/pending/{pending}/select", new { pageId })).EnsureSuccessStatusCode();
        return pending;
    }

    [Fact]
    public async Task Instagram_offers_only_the_Page_the_product_already_posts_to_even_when_Facebooks_list_leaves_it_out_and_the_login_is_recorded()
    {
        var admin = await host.LoginAdminAsync();

        // a product whose Facebook Page the Pages list will not return
        var hidden = await ProductAsync(admin);
        FakeFacebookGraph.ListIncludesHidden = true;
        try { await ConnectFacebookPageAsync(admin, hidden, "333"); } finally { FakeFacebookGraph.ListIncludesHidden = false; }
        var pending = await PendingAsync(admin, hidden, "&kind=instagram");
        var offered = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/connect/pending/{pending}", Json);
        Assert.Equal(["Hidden Page"], offered.EnumerateArray().Select(p => p.GetProperty("name").GetString()));          // found by asking for it directly, and nothing else is offered
        var sel = await admin.PostAsJsonAsync($"/api/v1/publishing/connect/pending/{pending}/select", new { pageId = "333" });
        Assert.Equal(HttpStatusCode.OK, sel.StatusCode);

        // another product: its own Page is offered, not the other Pages of the same login
        var other = await ProductAsync(admin);
        await ConnectFacebookPageAsync(admin, other, "111");
        var p2 = await PendingAsync(admin, other, "&kind=instagram");
        var offered2 = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/connect/pending/{p2}", Json);
        Assert.Equal(["Ludo Fight Official"], offered2.EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={hidden}", Json);
        var entry = audit.GetProperty("items").EnumerateArray().First(e => e.GetProperty("action").GetString() == "publishing.connect.diagnostics");
        Assert.Contains("Test Owner", entry.GetRawText());
    }
}
