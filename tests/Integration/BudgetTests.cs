using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class BudgetTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();
    private string Day(int back) => DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime).AddDays(-back).ToString("yyyy-MM-dd");

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Bud " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"bud-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private async Task<Guid> DraftAsync(HttpClient c, Guid pid, decimal cap = 1000, string currency = "USD", object[]? allocations = null, string risk = "Balanced")
    {
        var r = await c.PostAsJsonAsync("/api/v1/budget/budgets", new
        {
            scope = "Product", productId = pid, name = "Q4 " + Uniq(), currency, periodStart = Day(1), periodEnd = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime).AddDays(56).ToString("yyyy-MM-dd"),
            hardCap = cap, risk, allocations = allocations ?? [new { label = "Facebook ads", amount = cap * 0.6m }, new { label = "Google ads", amount = cap * 0.4m }],
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> BudgetAsync(HttpClient c, Guid id) => (await c.GetFromJsonAsync<JsonElement>($"/api/v1/budget/budgets/{id}", Json)).GetProperty("budget");

    private static async Task<Guid> RequestAsync(HttpClient c, Guid id, string kind, object? extra = null, string reason = "Planned campaign")
    {
        var body = new Dictionary<string, object?> { ["kind"] = kind, ["reason"] = reason };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var r = await c.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/changes", body);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient approver, HttpClient reader, Guid changeId, string decision, string? comment = null)
    {
        var ap = (await reader.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={changeId}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();
        return await approver.PostAsJsonAsync($"/api/v1/approvals/{ap}/decision", new { decision, comment });
    }

    private static Task<HttpResponseMessage> Spend(HttpClient c, Guid id, string kind, decimal amount, Guid? allocation = null)
        => c.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/spend", new { kind, amount, allocationId = allocation });

    /// <summary>A budget that has been launched through the approval flow.</summary>
    private async Task<(HttpClient Admin, HttpClient Approver, Guid Product, Guid Budget)> LaunchedAsync(decimal cap = 1000, string currency = "USD", string risk = "Balanced")
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Platform Administrator");
        var pid = await ProductAsync(admin); var id = await DraftAsync(admin, pid, cap, currency, risk: risk);
        var change = await RequestAsync(admin, id, "Launch");
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(approver, admin, change, "Approved")).StatusCode);
        Assert.Equal("Approved", (await BudgetAsync(admin, id)).GetProperty("status").GetString());
        return (admin, approver, pid, id);
    }

    [Fact]
    public async Task A_budget_is_a_draft_until_an_approval_launches_it_and_only_recommendations_are_allowed_before_that()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Platform Administrator");
        var pid = await ProductAsync(admin); var id = await DraftAsync(admin, pid);
        Assert.Equal(HttpStatusCode.Conflict, (await Spend(admin, id, "Actual", 10)).StatusCode);              // no real spend before launch
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Recommended", 500)).StatusCode);          // suggestions are fine

        var change = await RequestAsync(admin, id, "Launch");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/changes", new { kind = "Launch", reason = "again" })).StatusCode);   // one pending request at a time
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(admin, admin, change, "Approved")).StatusCode);         // four-eyes: the requester cannot approve
        Assert.Equal("Draft", (await BudgetAsync(admin, id)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(approver, admin, change, "Approved")).StatusCode);
        var b = await BudgetAsync(admin, id);
        Assert.Equal("Approved", b.GetProperty("status").GetString()); Assert.Equal(1000m, b.GetProperty("hardCap").GetDecimal()); Assert.Equal(2, b.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 10)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/budget/budgets/{id}", new { scope = "Product", productId = pid, name = "x", currency = "USD", periodStart = Day(1), periodEnd = Day(-30), hardCap = 99999 })).StatusCode);   // no direct edits after launch
    }

    [Fact]
    public async Task The_cap_is_enforced_by_the_backend_and_the_four_states_stay_separate()
    {
        var (admin, _, _, id) = await LaunchedAsync(1000);
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Committed", 300)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 600)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Approved", 900)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Recommended", 5000)).StatusCode);                  // recommendations never count against the cap
        var over = await Spend(admin, id, "Actual", 100.01m);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("hard cap", (await over.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Spend(admin, id, "Approved", 101)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 100)).StatusCode);                        // exactly the cap is allowed

        var t = (await BudgetAsync(admin, id)).GetProperty("totals");
        Assert.Equal(5000m, t.GetProperty("recommended").GetDecimal()); Assert.Equal(900m, t.GetProperty("approved").GetDecimal());
        Assert.Equal(300m, t.GetProperty("committed").GetDecimal()); Assert.Equal(700m, t.GetProperty("actual").GetDecimal());
        Assert.Equal(1000m, t.GetProperty("exposure").GetDecimal()); Assert.Equal(0m, t.GetProperty("remaining").GetDecimal());
    }

    [Fact]
    public async Task Concurrent_spending_can_never_push_a_budget_past_its_cap()
    {
        var (admin, _, _, id) = await LaunchedAsync(100);
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(async () => (await Spend(admin, id, "Actual", 10)).StatusCode)));
        Assert.Equal(10, results.Count(r => r == HttpStatusCode.Created));
        Assert.Equal(14, results.Count(r => r == HttpStatusCode.Conflict));
        Assert.Equal(100m, (await BudgetAsync(admin, id)).GetProperty("totals").GetProperty("exposure").GetDecimal());
    }

    [Fact]
    public async Task Raising_the_cap_needs_its_own_approval_for_exactly_the_requested_amount()
    {
        var (admin, approver, _, id) = await LaunchedAsync(1000);
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 1000)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Spend(admin, id, "Actual", 1)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/changes", new { kind = "Increase", proposedCap = 900, reason = "lower" })).StatusCode);   // must be higher
        var inc = await RequestAsync(admin, id, "Increase", new { proposedCap = 1500m });
        Assert.Equal(HttpStatusCode.Conflict, (await Spend(admin, id, "Actual", 1)).StatusCode);                          // a pending request raises nothing
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(approver, admin, inc, "Rejected", "Not now")).StatusCode);
        Assert.Equal(1000m, (await BudgetAsync(admin, id)).GetProperty("hardCap").GetDecimal());                          // a rejection leaves the cap alone

        var inc2 = await RequestAsync(admin, id, "Increase", new { proposedCap = 1500m });
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(approver, admin, inc2, "Approved")).StatusCode);
        var b = await BudgetAsync(admin, id);
        Assert.Equal(1500m, b.GetProperty("hardCap").GetDecimal());
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 500)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Spend(admin, id, "Actual", 1)).StatusCode);                          // the new cap holds
    }

    [Fact]
    public async Task Reallocation_moves_money_between_lines_only_with_approval_and_never_below_what_a_line_has_used()
    {
        var (admin, approver, _, id) = await LaunchedAsync(1000);
        var lines = (await BudgetAsync(admin, id)).GetProperty("allocations").EnumerateArray().ToDictionary(a => a.GetProperty("label").GetString()!, a => a.GetProperty("id").GetGuid());   // Facebook 600, Google 400
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 600, lines["Facebook ads"])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Spend(admin, id, "Actual", 1, lines["Facebook ads"])).StatusCode);   // the line is used up even though the budget is not

        object[] Alloc(decimal fb, decimal g) => [new { label = "Facebook ads", amount = fb }, new { label = "Google ads", amount = g }];
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/changes", new { kind = "Reallocation", allocations = Alloc(500, 500), reason = "shift" })).StatusCode);   // below Facebook's 600 used
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/changes", new { kind = "Reallocation", allocations = Alloc(700, 400), reason = "shift" })).StatusCode);   // over the cap
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/changes", new { kind = "Reallocation", allocations = new object[] { new { label = "Google ads", amount = 400 } }, reason = "drop fb" })).StatusCode);   // cannot drop a used line

        var re = await RequestAsync(admin, id, "Reallocation", new { allocations = Alloc(700, 300) });
        Assert.Equal(HttpStatusCode.Conflict, (await Spend(admin, id, "Actual", 1, lines["Facebook ads"])).StatusCode);   // nothing moves before approval
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(approver, admin, re, "Approved")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 100, lines["Facebook ads"])).StatusCode);   // the moved money is now usable
        var allocs = (await BudgetAsync(admin, id)).GetProperty("allocations").EnumerateArray().ToDictionary(a => a.GetProperty("label").GetString()!, a => a.GetProperty("amount").GetDecimal());
        Assert.Equal(700m, allocs["Facebook ads"]); Assert.Equal(300m, allocs["Google ads"]);
        Assert.Equal(lines["Facebook ads"], (await BudgetAsync(admin, id)).GetProperty("allocations").EnumerateArray().First(a => a.GetProperty("label").GetString() == "Facebook ads").GetProperty("id").GetGuid());   // history stays attached
    }

    [Fact]
    public async Task A_withdrawn_request_frees_the_budget_for_a_new_one_and_cannot_be_approved()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Platform Administrator");
        var pid = await ProductAsync(admin); var id = await DraftAsync(admin, pid);
        var change = await RequestAsync(admin, id, "Launch");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/budget/changes/{change}/withdraw", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/budget/changes/{change}/withdraw", null)).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={change}", Json)).EnumerateArray());
        var again = await RequestAsync(admin, id, "Launch");
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(approver, admin, again, "Approved")).StatusCode);
    }

    [Fact]
    public async Task Crossing_the_warning_and_stop_thresholds_raises_one_alert_each_and_voiding_re_arms_them()
    {
        var (admin, _, pid, id) = await LaunchedAsync(1000);
        var wf = await admin.PostAsJsonAsync("/api/v1/automation/workflows", new
        {
            name = "Budget alert " + Uniq(), trigger = "budget.threshold-crossed", productId = pid, enabled = true,
            definition = new { steps = new object[] { new { kind = "task", title = "Budget {{name}} is at {{spentPct}}% ({{level}})", assignee = "none" } } },
        });
        Assert.Equal(HttpStatusCode.Created, wf.StatusCode);
        var runner = host.Services.GetRequiredService<JobRunner>();
        async Task Drain() { for (var i = 0; i < 100 && await runner.RunOnceAsync(default); i++) { } }
        var name = (await BudgetAsync(admin, id)).GetProperty("name").GetString();
        async Task<List<string>> Alerts() => (await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks?scope=all", Json)).EnumerateArray().Select(t => t.GetProperty("title").GetString()!).Where(t => t.StartsWith($"Budget {name} is at")).ToList();

        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 700)).StatusCode); await Drain();
        Assert.Empty(await Alerts());                                                  // 70%: quiet
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 150)).StatusCode); await Drain();
        Assert.Single(await Alerts());                                                 // 85%: warning
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 20)).StatusCode); await Drain();
        Assert.Single(await Alerts());                                                 // still a warning: no repeat
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 130)).StatusCode); await Drain();
        var both = await Alerts();
        Assert.Equal(2, both.Count); Assert.Contains(both, a => a.Contains("(Stop)")); Assert.Contains(both, a => a.Contains("100"));

        // Voiding the last entry drops back below the stop line; reaching it again alerts again.
        var spend = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/budget/budgets/{id}/spend", Json)).EnumerateArray().First(s => s.GetProperty("amount").GetDecimal() == 130m);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/budget/spend/{spend.GetProperty("id").GetGuid()}/void", new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/budget/spend/{spend.GetProperty("id").GetGuid()}/void", new { reason = "Entered twice" })).StatusCode);
        Assert.Equal(870m, (await BudgetAsync(admin, id)).GetProperty("totals").GetProperty("exposure").GetDecimal());   // voided entries stop counting but stay in the history
        Assert.Equal(HttpStatusCode.Created, (await Spend(admin, id, "Actual", 130)).StatusCode); await Drain();
        Assert.Equal(3, (await Alerts()).Count);
    }

    // ---- planner ----

    [Fact]
    public async Task With_no_recorded_history_the_planner_proposes_a_learning_experiment_not_a_forecast()
    {
        var (admin, _, _, id) = await LaunchedAsync(2000, risk: "Conservative");
        var r = await (await admin.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/plan", new { resultMetric = "leads" })).Content.ReadFromJsonAsync<JsonElement>(Json);
        var o = r.GetProperty("output");
        Assert.Equal("LearningExperiment", o.GetProperty("mode").GetString());
        Assert.Equal(0, o.GetProperty("scenarios").GetArrayLength());
        Assert.Equal(100m, o.GetProperty("experiment").GetProperty("budget").GetDecimal());                              // 5% of 2,000 for a conservative appetite
        Assert.Equal(["Facebook ads", "Google ads"], o.GetProperty("experiment").GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
        Assert.NotEmpty(o.GetProperty("experiment").GetProperty("stopGo").EnumerateArray());

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/budget/plans/{r.GetProperty("planId").GetGuid()}/adopt", new { scenario = "Balanced" })).StatusCode);   // nothing to adopt
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/budget/budgets/{id}/plans", Json)).EnumerateArray());
    }

    [Fact]
    public async Task With_enough_imported_history_the_planner_forecasts_ranges_and_adopting_records_only_recommendations()
    {
        var (admin, _, pid, id) = await LaunchedAsync(5000);
        var src = (await (await admin.PostAsJsonAsync("/api/v1/analytics/sources", new { productId = pid, name = "Ads export " + Uniq(), kind = "Manual" })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var csv = new StringBuilder("metric,periodStart,periodEnd,value,channel,currency\n");
        for (var w = 1; w <= 10; w++)
        {
            csv.AppendLine($"spend,{Day(7 * w + 5)},{Day(7 * w - 1)},{400 + (w % 3) * 40},Facebook ads,USD");
            csv.AppendLine($"leads,{Day(7 * w + 5)},{Day(7 * w - 1)},{20 + w % 4},Facebook ads,");
            csv.AppendLine($"spend,{Day(7 * w + 5)},{Day(7 * w - 1)},9999,Facebook ads,PKR");          // another currency must be ignored, not converted
        }
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv.ToString())), "file", "ads.csv" } };
        var imp = await (await admin.PostAsync($"/api/v1/analytics/products/{pid}/imports?sourceId={src}", form)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(30, imp.GetProperty("imported").GetInt32());

        var r = await (await admin.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/plan", new { resultMetric = "leads" })).Content.ReadFromJsonAsync<JsonElement>(Json);
        var o = r.GetProperty("output");
        Assert.Equal("Forecast", o.GetProperty("mode").GetString());
        Assert.Equal(["Conservative", "Balanced", "Growth"], o.GetProperty("scenarios").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Contains(o.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("PKR"));
        var fb = o.GetProperty("scenarios")[1].GetProperty("channels").EnumerateArray().First(c => c.GetProperty("channel").GetString() == "Facebook ads");
        Assert.True(fb.GetProperty("resultsLow").GetInt32() < fb.GetProperty("resultsHigh").GetInt32());
        var ev = o.GetProperty("evidence").EnumerateArray().First(e => e.GetProperty("channel").GetString() == "Facebook ads");
        Assert.Equal(10, ev.GetProperty("weeksUsed").GetInt32()); Assert.True(ev.GetProperty("qualified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, o.GetProperty("experiment").ValueKind);

        var before = (await BudgetAsync(admin, id)).GetProperty("totals");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/budget/plans/{r.GetProperty("planId").GetGuid()}/adopt", new { scenario = "Balanced" })).StatusCode);
        var after = (await BudgetAsync(admin, id)).GetProperty("totals");
        Assert.True(after.GetProperty("recommended").GetDecimal() > 0);
        Assert.Equal(before.GetProperty("exposure").GetDecimal(), after.GetProperty("exposure").GetDecimal());     // adopting spends nothing
        Assert.Equal(before.GetProperty("approved").GetDecimal(), after.GetProperty("approved").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/budget/plans/{r.GetProperty("planId").GetGuid()}/adopt", new { scenario = "Reckless" })).StatusCode);
    }

    // ---- access ----

    [Fact]
    public async Task Reading_proposing_and_approving_are_separate_permissions()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin); var id = await DraftAsync(admin, pid);
        var viewer = await UserAsync(admin, "Read-only Viewer"); var manager = await UserAsync(admin, "Marketing Manager"); var approverRole = await UserAsync(admin, "Approver");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/budget/budgets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/budget/budgets", new { scope = "Product", productId = pid, name = "x", currency = "USD", periodStart = Day(1), periodEnd = Day(-5), hardCap = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/budget/budgets/{id}/plan", new { })).StatusCode);

        var change = await RequestAsync(manager, id, "Launch");
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(manager, admin, change, "Approved")).StatusCode);       // can propose, cannot approve
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(approverRole, admin, change, "Approved")).StatusCode);  // content approvers do not approve money
        Assert.Equal("Draft", (await BudgetAsync(admin, id)).GetProperty("status").GetString());

        var scopedEmail = $"bud2-{Uniq()}@test.local"; var other = await ProductAsync(admin);
        (await admin.PostAsJsonAsync("/api/v1/users", new { email = scopedEmail, displayName = "S", password = "Some-Strong-Pass-123", roles = new[] { "Marketing Manager" }, allProducts = false, productIds = new[] { other } })).EnsureSuccessStatusCode();
        var scoped = await host.LoginAsync(scopedEmail, "Some-Strong-Pass-123");
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/v1/budget/budgets/{id}")).StatusCode);        // another product's budget does not exist for them
        Assert.Equal(HttpStatusCode.BadRequest, (await scoped.PostAsJsonAsync("/api/v1/budget/budgets", new { scope = "Company", name = "All", currency = "USD", periodStart = Day(1), periodEnd = Day(-5), hardCap = 10 })).StatusCode);   // company-wide needs every product
    }

    [Fact]
    public async Task Budget_inputs_are_validated()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        object Body(object? cap = null, string currency = "USD", string? start = null, string? end = null, object[]? allocs = null, int? warn = null, int? stop = null)
            => new { scope = "Product", productId = pid, name = "V", currency, periodStart = start ?? Day(1), periodEnd = end ?? Day(-10), hardCap = cap ?? 100, allocations = allocs, warnAtPct = warn, stopAtPct = stop };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/budget/budgets", Body(cap: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/budget/budgets", Body(currency: "DOLLARS"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/budget/budgets", Body(start: Day(-10), end: Day(1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/budget/budgets", Body(allocs: [new { label = "A", amount = 101 }]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/budget/budgets", Body(warn: 90, stop: 80))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/budget/budgets", Body(allocs: [new { label = "A", amount = 100 }]))).StatusCode);
    }
}
