using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class StrategyTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First().GetProperty("id").GetGuid();
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Strat " + Guid.NewGuid().ToString("N")[..6], categoryId = catId });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Templates_differ_for_gaming_and_b2b()
    {
        var admin = await host.LoginAdminAsync();
        var t = await admin.GetFromJsonAsync<JsonElement>("/api/v1/strategy-templates", Json);
        string Kpis(string key) => string.Join(",", t.EnumerateArray().First(x => x.GetProperty("key").GetString() == key).GetProperty("starter").GetProperty("kpis").EnumerateArray().Select(k => k.GetProperty("name").GetString()));
        Assert.Contains("Day-1 retention", Kpis("b2c-gaming"));
        Assert.Contains("Demos booked", Kpis("b2b-software"));
        Assert.DoesNotContain("Day-1 retention", Kpis("b2b-software"));
    }

    [Fact]
    public async Task Versions_are_immutable_history_with_optimistic_concurrency()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var create = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/strategies", new { name = "Launch", templateKey = "b2c-gaming" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var sid = (await create.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        var v1 = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/strategies/{sid}", Json);
        Assert.Equal(1, v1.GetProperty("currentVersion").GetInt32());
        Assert.True(v1.GetProperty("current").GetProperty("kpis").GetArrayLength() > 0);   // starter KPIs applied

        var save = await admin.PostAsJsonAsync($"/api/v1/strategies/{sid}/versions", new { baseVersion = 1, note = "set objective", content = new { objective = "10k installs", budgetUsd = 100, kpis = new[] { new { name = "Installs", target = "10000" } } } });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // a second editor still on v1 is rejected rather than silently overwriting
        var stale = await admin.PostAsJsonAsync($"/api/v1/strategies/{sid}/versions", new { baseVersion = 1, content = new { objective = "other" } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var now = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/strategies/{sid}", Json);
        Assert.Equal(2, now.GetProperty("currentVersion").GetInt32());
        Assert.Equal("10k installs", now.GetProperty("current").GetProperty("objective").GetString());
        var old = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/strategies/{sid}/versions/1", Json);
        Assert.Equal("", old.GetProperty("content").GetProperty("objective").GetString());          // v1 untouched
        Assert.Equal(2, now.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_content_is_rejected_and_archived_strategies_are_read_only()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var sid = (await (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/strategies", new { name = "S" })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/strategies/{sid}/versions", new { baseVersion = 1, content = new { budgetUsd = -5 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/strategies/{sid}/versions", new { baseVersion = 1, content = new { hypotheses = new[] { new { statement = "x", status = "Maybe" } } } })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/strategies/{sid}/archive/true", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/strategies/{sid}/versions", new { baseVersion = 1, content = new { objective = "x" } })).StatusCode);
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/strategies", Json)).GetArrayLength());
    }

    [Fact]
    public async Task Scope_and_permissions_apply()
    {
        var admin = await host.LoginAdminAsync();
        var a = await ProductAsync(admin); var b = await ProductAsync(admin);
        var sid = (await (await admin.PostAsJsonAsync($"/api/v1/products/{b}/strategies", new { name = "Secret" })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        async Task<HttpClient> User(string role, bool all, Guid[]? products = null)
        {
            var email = $"s-{Guid.NewGuid():N}@test.local";
            (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = all, productIds = products })).EnsureSuccessStatusCode();
            return await host.LoginAsync(email, "Some-Strong-Pass-123");
        }

        var mm = await User("Marketing Manager", false, [a]);
        Assert.Equal(HttpStatusCode.NotFound, (await mm.GetAsync($"/api/v1/strategies/{sid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await mm.PostAsJsonAsync($"/api/v1/products/{a}/strategies", new { name = "Mine" })).StatusCode);

        var viewer = await User("Read-only Viewer", true);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/strategies/{sid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/products/{b}/strategies", new { name = "No" })).StatusCode);
    }
}
