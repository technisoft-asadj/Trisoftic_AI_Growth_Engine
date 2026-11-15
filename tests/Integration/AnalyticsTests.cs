using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class AnalyticsTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();
    private string Day(int back) => DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime).AddDays(-back).ToString("yyyy-MM-dd");

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "An " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SourceAsync(HttpClient admin, Guid pid, string kind = "Manual", int? freshness = null, string? name = null)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/analytics/sources", new { productId = pid, name = name ?? "Sheet " + Uniq(), kind, freshnessHours = freshness });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient admin, Guid pid, Guid source, string csv, bool dry = false, string fileName = "data.csv")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName } };
        return await admin.PostAsync($"/api/v1/analytics/products/{pid}/imports?sourceId={source}&dryRun={dry.ToString().ToLower()}", form);
    }

    private static async Task<JsonElement> Tile(HttpClient c, Guid pid, string key, string query = "")
    {
        var s = await c.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/summary?productId={pid}{query}", Json);
        return s.GetProperty("tiles").EnumerateArray().First(t => t.GetProperty("key").GetString() == key);
    }

    [Fact]
    public async Task An_empty_product_shows_every_metric_as_NoData_with_no_values_and_definitions()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var s = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/summary?productId={pid}", Json);
        var tiles = s.GetProperty("tiles").EnumerateArray().ToList();
        Assert.True(tiles.Count >= 15);
        Assert.All(tiles, t =>
        {
            Assert.Equal("NoData", t.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, t.GetProperty("value").ValueKind);        // never 0
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("definition").GetString()));
        });
        Assert.Contains("not zero", string.Join(" ", s.GetProperty("meta").GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
    }

    [Fact]
    public async Task A_dry_run_validates_without_saving_and_the_real_import_reports_bad_rows_instead_of_dropping_them()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin); var src = await SourceAsync(admin, pid);
        var csv = $"metric,periodStart,periodEnd,value,channel\nimpressions,{Day(6)},{Day(0)},12000,facebook\nimpressions,{Day(6)},{Day(0)},oops,instagram\nnonsense,{Day(6)},{Day(0)},5,facebook\nreach,{Day(6)},{Day(0)},,facebook\n";

        var dry = await (await ImportAsync(admin, pid, src, csv, dry: true)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, dry.GetProperty("wouldImport").GetInt32()); Assert.Equal(2, dry.GetProperty("rejected").GetInt32()); Assert.Equal(0, dry.GetProperty("imported").GetInt32());
        Assert.Equal("NoData", (await Tile(admin, pid, "impressions")).GetProperty("status").GetString());      // nothing was written

        var real = await (await ImportAsync(admin, pid, src, csv)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(2, real.GetProperty("imported").GetInt32());
        var errors = real.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal([3, 4], errors.Select(e => e.GetProperty("row").GetInt32()));
        Assert.Contains("not a number", errors[0].GetProperty("message").GetString());

        var impressions = await Tile(admin, pid, "impressions");
        Assert.Equal("Measured", impressions.GetProperty("status").GetString()); Assert.Equal(12000m, impressions.GetProperty("value").GetDecimal());
        var reach = await Tile(admin, pid, "reach");
        Assert.Equal("Unavailable", reach.GetProperty("status").GetString());                                      // blank = could not be measured, not zero
        Assert.Equal(JsonValueKind.Null, reach.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Existing_figures_are_not_overwritten_and_rolling_back_a_batch_removes_exactly_its_rows()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin); var src = await SourceAsync(admin, pid);
        var first = await (await ImportAsync(admin, pid, src, $"metric,periodStart,value\nclicks,{Day(3)},100\nclicks,{Day(2)},200\n")).Content.ReadFromJsonAsync<JsonElement>(Json);
        var batch = first.GetProperty("batchId").GetGuid();

        var again = await (await ImportAsync(admin, pid, src, $"metric,periodStart,value\nclicks,{Day(3)},999\nclicks,{Day(1)},50\n")).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(1, again.GetProperty("imported").GetInt32());
        Assert.Contains("already stored", again.GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.Equal(350m, (await Tile(admin, pid, "clicks")).GetProperty("value").GetDecimal());               // 100 + 200 + 50; the 999 never landed

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/analytics/imports/{batch}/rollback", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/analytics/imports/{batch}/rollback", null)).StatusCode);   // harmless to repeat
        Assert.Equal(50m, (await Tile(admin, pid, "clicks")).GetProperty("value").GetDecimal());               // only the second batch remains

        var fixedUp = await (await ImportAsync(admin, pid, src, $"metric,periodStart,value\nclicks,{Day(3)},111\n")).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(1, fixedUp.GetProperty("imported").GetInt32());                                            // after the rollback the corrected figure is accepted
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/products/{pid}/imports", Json);
        Assert.Contains(history.EnumerateArray(), b => b.GetProperty("status").GetString() == "RolledBack");
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={pid}&take=100", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "analytics.import.rolled-back");
    }

    [Fact]
    public async Task Money_stays_in_its_own_currency_and_two_sources_do_not_double_count()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var manual = await SourceAsync(admin, pid, name: "Manual " + Uniq()); var platform = await SourceAsync(admin, pid, "Platform", name: "Platform " + Uniq());
        await ImportAsync(admin, pid, manual, $"metric,periodStart,value,currency\nspend,{Day(2)},100,USD\nspend,{Day(2)},9000,PKR\nimpressions,{Day(2)},500,\n");
        Clock.Advance(TimeSpan.FromMinutes(5));
        await ImportAsync(admin, pid, platform, $"metric,periodStart,value,currency\nimpressions,{Day(2)},520,\nspend,{Day(1)},40,USD\n");

        var spend = await Tile(admin, pid, "spend");
        Assert.Equal(JsonValueKind.Null, spend.GetProperty("value").ValueKind);
        var byCur = spend.GetProperty("valuesByCurrency").EnumerateArray().ToDictionary(c => c.GetProperty("currency").GetString()!, c => c.GetProperty("value").GetDecimal());
        Assert.Equal(140m, byCur["USD"]); Assert.Equal(9000m, byCur["PKR"]);

        var imp = await Tile(admin, pid, "impressions");
        Assert.Equal(520m, imp.GetProperty("value").GetDecimal());          // the later retrieval wins; not 500 + 520
        Assert.Equal(1, imp.GetProperty("conflicts").GetInt32());
    }

    [Fact]
    public async Task Filters_limit_by_channel_and_period_and_the_series_lists_each_period()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin); var src = await SourceAsync(admin, pid);
        await ImportAsync(admin, pid, src, $"metric,periodStart,value,channel\nimpressions,{Day(5)},100,facebook\nimpressions,{Day(5)},300,instagram\nimpressions,{Day(40)},999,facebook\n");
        Assert.Equal(400m, (await Tile(admin, pid, "impressions")).GetProperty("value").GetDecimal());                    // default 30 days excludes day 40
        Assert.Equal(100m, (await Tile(admin, pid, "impressions", "&channel=Facebook")).GetProperty("value").GetDecimal());   // case-insensitive
        Assert.Equal(1399m, (await Tile(admin, pid, "impressions", $"&from={Day(60)}&to={Day(0)}")).GetProperty("value").GetDecimal());
        var series = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/series?productId={pid}&metric=impressions&from={Day(60)}&to={Day(0)}", Json);
        Assert.Equal(3, series.GetArrayLength());
    }

    [Fact]
    public async Task A_source_goes_stale_after_its_window_and_a_stale_event_is_raised_once()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var src = await SourceAsync(admin, pid, freshness: 24);
        await ImportAsync(admin, pid, src, $"metric,periodStart,value\nclicks,{Day(1)},10\n");
        Assert.False((await Tile(admin, pid, "clicks")).GetProperty("stale").GetBoolean());

        // A workflow that reacts to staleness proves the event reaches other modules.
        var wf = await admin.PostAsJsonAsync("/api/v1/automation/workflows", new { name = "Stale " + Uniq(), trigger = "analytics.source-stale", productId = pid, enabled = true, definition = new { steps = new object[] { new { kind = "task", title = "Refresh {{name}}", assignee = "none" } } } });
        Assert.Equal(HttpStatusCode.Created, wf.StatusCode);

        Clock.Advance(TimeSpan.FromHours(30));
        Assert.True((await Tile(admin, pid, "clicks")).GetProperty("stale").GetBoolean());

        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 2; i++)
        {
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("analytics.freshness-check", "{}", IdempotencyKey: "fc-" + Guid.NewGuid()));
            for (var k = 0; k < 100 && await runner.RunOnceAsync(default); k++) { }
        }
        var tasks = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks?scope=all", Json)).EnumerateArray().Where(t => t.GetProperty("title").GetString()!.StartsWith("Refresh Sheet")).ToList();
        Assert.Single(tasks);                                                // two checks, one alert
        var sources = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/sources?productId={pid}", Json);
        Assert.True(sources[0].GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Other_modules_can_record_measurements_and_read_them_back_through_the_contracts()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        using var scope = host.Services.CreateScope();
        var sink = scope.ServiceProvider.GetRequiredService<IMetricSink>(); var reader = scope.ServiceProvider.GetRequiredService<IMetricReader>();
        var d = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime).AddDays(-2);
        var stored = await sink.RecordAsync("facebook-page:Demo", "Social", pid, [
            new MetricPoint(pid, "impressions", d, d, 700m, "facebook"), new MetricPoint(pid, "reach", d, d, null, "facebook"),
            new MetricPoint(pid, "spend", d, d, 5m, "facebook", Currency: null),            // money without a currency is refused
            new MetricPoint(pid, "unknown_metric", d, d, 1m)], Clock.UtcNow);
        Assert.Equal(2, stored);

        await sink.RecordAsync("facebook-page:Demo", "Social", pid, [new MetricPoint(pid, "impressions", d, d, 710m, "facebook")], Clock.UtcNow.AddHours(1));   // a refreshed pull replaces
        var read = await reader.QueryAsync(pid, ["impressions", "reach"], d.AddDays(-1), d.AddDays(1));
        Assert.Equal(710m, read.Single(p => p.MetricKey == "impressions").Value);
        Assert.Null(read.Single(p => p.MetricKey == "reach").Value);

        var sources = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/sources?productId={pid}", Json);
        Assert.Equal("Social", sources[0].GetProperty("kind").GetString());
    }


    [Fact]
    public async Task Pulled_social_metrics_land_in_analytics_with_their_source_and_an_adapter_without_insights_claims_nothing()
    {
        var admin = await host.LoginAdminAsync(); var withData = await ProductAsync(admin); var without = await ProductAsync(admin);
        foreach (var p in new[] { withData, without }) Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{p}/accounts/sandbox", new { channel = "facebook", name = "Pull page" })).StatusCode);
        var yesterday = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime).AddDays(-1);
        var runner = host.Services.GetRequiredService<JobRunner>();
        async Task Pull()
        {
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("publishing.pull-insights", "{}", IdempotencyKey: "pull-" + Guid.NewGuid()));
            for (var k = 0; k < 100 && await runner.RunOnceAsync(default); k++) { }
        }

        ScriptedPublisher.Insights = null;                                   // the adapter cannot supply insights
        await Pull();
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/sources?productId={without}", Json)).EnumerateArray());   // no source, no false freshness

        ScriptedPublisher.Insights = [new("impressions", yesterday, 900m), new("reach", yesterday, null)];
        try { await Pull(); }
        finally { ScriptedPublisher.Insights = null; }
        var sources = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/sources?productId={withData}", Json)).EnumerateArray().ToList();
        var src = Assert.Single(sources);
        Assert.Equal("sandbox:Pull page", src.GetProperty("name").GetString()); Assert.Equal("Social", src.GetProperty("kind").GetString()); Assert.False(src.GetProperty("stale").GetBoolean());
        Assert.Equal(900m, (await Tile(admin, withData, "impressions", "&channel=facebook")).GetProperty("value").GetDecimal());
        Assert.Equal("Unavailable", (await Tile(admin, withData, "reach")).GetProperty("status").GetString());   // the network could not provide it: not zero
    }
    [Fact]
    public async Task Custom_metrics_need_a_definition_and_access_is_limited_by_permission_and_product()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var key = "custom_" + Uniq().ToLower();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/analytics/metrics", new { key = "Bad Key", name = "x", unit = "Count", aggregation = "Sum", direction = "Neutral", definition = "d" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/analytics/metrics", new { key, name = "Tournament entries", unit = "Count", aggregation = "Sum", direction = "HigherIsBetter", definition = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/analytics/metrics", new { key, name = "Tournament entries", unit = "Count", aggregation = "Sum", direction = "HigherIsBetter", definition = "Players who entered a paid tournament." })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/analytics/metrics", new { key = "impressions", name = "x", unit = "Count", aggregation = "Sum", direction = "Neutral", definition = "d" })).StatusCode);

        var src = await SourceAsync(admin, pid);
        Assert.Equal(1, (await (await ImportAsync(admin, pid, src, $"metric,periodStart,value\n{key},{Day(1)},40\n")).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("imported").GetInt32());

        var email = $"an-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "V", password = "Some-Strong-Pass-123", roles = new[] { "Read-only Viewer" }, allProducts = true })).EnsureSuccessStatusCode();
        var viewer = await host.LoginAsync(email, "Some-Strong-Pass-123");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/analytics/summary?productId={pid}")).StatusCode);          // may read
        Assert.Equal(HttpStatusCode.NotFound, (await ImportAsync(viewer, pid, src, "metric,periodStart,value\nclicks,2026-01-01,1\n")).StatusCode);    // may not import
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/analytics/metrics", new { key = "nope_1", name = "x", unit = "Count", aggregation = "Sum", direction = "Neutral", definition = "d" })).StatusCode);

        // A user limited to a different product cannot see this product's numbers at all.
        var other = await ProductAsync(admin);
        var scopedEmail = $"an2-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email = scopedEmail, displayName = "S", password = "Some-Strong-Pass-123", roles = new[] { "Analyst" }, allProducts = false, productIds = new[] { other } })).EnsureSuccessStatusCode();
        var scoped = await host.LoginAsync(scopedEmail, "Some-Strong-Pass-123");
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/v1/analytics/summary?productId={pid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scoped.GetAsync($"/api/v1/analytics/summary?productId={other}")).StatusCode);
    }
}
