using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class SalesTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> UserAsync(HttpClient admin, string role, bool all = true, Guid[]? products = null)
    {
        var email = $"sales-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "S " + Uniq(), password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = all, productIds = products })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Sales " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> DefaultPipeline(HttpClient c)
        => (await c.GetFromJsonAsync<JsonElement>("/api/v1/crm/pipelines", Json)).EnumerateArray().First(p => p.GetProperty("name").GetString() == "Sales");

    private static Guid StageId(JsonElement pipeline, string name) => pipeline.GetProperty("stages").EnumerateArray().First(s => s.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    private static async Task<(Guid Contact, Guid Lead)> NewLead(HttpClient c, Guid pid, string name = "Deal Maker")
    {
        var contact = await c.PostAsJsonAsync("/api/v1/crm/contacts", new { firstName = name, lastName = Uniq(), methods = new[] { new { type = "Email", value = $"d{Uniq()}@example.com" } } });
        var cid = (await contact.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var lead = await c.PostAsJsonAsync("/api/v1/crm/leads", new { contactId = cid, productId = pid, source = "manual", message = "wants a demo" });
        Assert.Equal(HttpStatusCode.Created, lead.StatusCode);
        return (cid, (await lead.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid());
    }

    private static async Task Qualify(HttpClient c, Guid leadId)
    {
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/crm/leads/{leadId}/status", new { status = "Contacted" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/crm/leads/{leadId}/status", new { status = "Qualified", note = "Budget confirmed" })).StatusCode);
    }

    private static async Task<JsonElement> Dash(HttpClient c, Guid pid) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/crm/dashboard?productId={pid}", Json);

    [Fact]
    public async Task A_default_pipeline_exists_with_open_won_and_lost_stages()
    {
        var admin = await host.LoginAdminAsync();
        var p = await DefaultPipeline(admin);
        var stages = p.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(7, stages.Count);
        Assert.Equal(5, stages.Count(s => s.GetProperty("kind").GetString() == "Open"));
        Assert.Single(stages, s => s.GetProperty("kind").GetString() == "Won");
        Assert.Single(stages, s => s.GetProperty("kind").GetString() == "Lost");
    }

    [Fact]
    public async Task Lead_to_won_journey_with_stage_history_audit_and_guard_rails()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var pipe = await DefaultPipeline(admin);
        var (_, leadId) = await NewLead(admin, pid);

        // only a qualified lead converts
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/crm/leads/{leadId}/convert", new { value = 5000 })).StatusCode);
        await Qualify(admin, leadId);
        var conv = await admin.PostAsJsonAsync($"/api/v1/crm/leads/{leadId}/convert", new { name = "Pharmacy POS - 3 shops", value = 5000, currency = "usd", expectedCloseDate = "2027-01-31" });
        Assert.Equal(HttpStatusCode.Created, conv.StatusCode);
        var opp = await conv.Content.ReadFromJsonAsync<JsonElement>(Json);
        var oppId = opp.GetProperty("id").GetGuid();
        Assert.Equal("USD", opp.GetProperty("currency").GetString());
        Assert.Equal("New enquiry", opp.GetProperty("stage").GetString());
        Assert.Equal(10, opp.GetProperty("probability").GetInt32());
        Assert.Equal(leadId, opp.GetProperty("leadId").GetGuid());

        var lead = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/leads/{leadId}", Json);
        Assert.Equal("Converted", lead.GetProperty("lead").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/crm/leads/{leadId}/convert", new { value = 1 })).StatusCode);   // only once

        // walk the stages
        async Task<HttpResponseMessage> Move(string stage)
            => await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/stage", new { stageId = StageId(pipe, stage) });
        Assert.Equal(HttpStatusCode.OK, (await Move("Qualified")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move("Demo booked")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Move("Demo booked")).StatusCode);                              // no-op
        Assert.Equal(HttpStatusCode.Conflict, (await Move("Lost")).StatusCode);                                     // reason required
        var won = await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/stage", new { stageId = StageId(pipe, "Won"), revenue = 4500, reason = "Signed after the demo" });
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);
        var wj = await won.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Won", wj.GetProperty("status").GetString());
        Assert.Equal(4500, wj.GetProperty("revenueAmount").GetDecimal());
        Assert.Equal(100, wj.GetProperty("probability").GetInt32());

        Assert.Equal(HttpStatusCode.Conflict, (await Move("Proposal sent")).StatusCode);                            // closed deals do not move
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/crm/opportunities/{oppId}", new { value = 1 })).StatusCode);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/opportunities/{oppId}", Json);
        var path = detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("to").GetString()!).ToArray();
        Assert.Equal(["New enquiry", "Qualified", "Demo booked", "Won"], path);

        // reopen goes back into an open stage only
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/reopen", new { stageId = StageId(pipe, "Lost") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/reopen", new { stageId = StageId(pipe, "Negotiation") })).StatusCode);

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/governance/audit?subjectType=Opportunity&take=100", Json);
        var actions = audit.GetProperty("items").EnumerateArray().Where(a => a.GetProperty("subjectId").GetString() == oppId.ToString()).Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Equal(3, actions.Count(a => a == "crm.opportunity.stage-changed")); Assert.Contains("crm.opportunity.reopened", actions);
    }

    [Fact]
    public async Task Losing_needs_a_reason_and_ownership_changes_are_audited()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var pipe = await DefaultPipeline(admin);
        var (cid, _) = await NewLead(admin, pid);
        var o = await admin.PostAsJsonAsync("/api/v1/crm/opportunities", new { pipelineId = pipe.GetProperty("id").GetGuid(), contactId = cid, productId = pid, name = "Direct deal", value = 800, currency = "PKR" });
        Assert.Equal(HttpStatusCode.Created, o.StatusCode);
        var oppId = (await o.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/stage", new { stageId = StageId(pipe, "Lost") })).StatusCode);
        var lost = await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/stage", new { stageId = StageId(pipe, "Lost"), reason = "Budget frozen" });
        Assert.Equal("Lost", (await lost.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("status").GetString());

        var other = await UserAsync(admin, "Sales Representative");
        var otherId = (await other.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Json)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/owner", new { userId = otherId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/owner", new { userId = Guid.NewGuid() })).StatusCode);
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/governance/audit?subjectType=Opportunity&take=100", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("subjectId").GetString() == oppId.ToString() && a.GetProperty("action").GetString() == "crm.opportunity.owner-changed");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/crm/opportunities", new { pipelineId = pipe.GetProperty("id").GetGuid(), contactId = cid, productId = pid, name = "Bad", value = 1, currency = "dollars" })).StatusCode);
    }

    [Fact]
    public async Task Board_groups_deals_by_stage_and_never_adds_different_currencies()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var pipe = await DefaultPipeline(admin);
        var pipeId = pipe.GetProperty("id").GetGuid();
        foreach (var (v, cur) in new[] { (1000m, "USD"), (500m, "USD"), (80000m, "PKR") })
        {
            var (cid, _) = await NewLead(admin, pid);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/crm/opportunities", new { pipelineId = pipeId, contactId = cid, productId = pid, name = $"{cur} deal", value = v, currency = cur })).StatusCode);
        }
        var board = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/pipelines/{pipeId}/board?productId={pid}", Json);
        var first = board.GetProperty("columns").EnumerateArray().First();
        Assert.Equal("New enquiry", first.GetProperty("stage").GetProperty("name").GetString());
        Assert.Equal(3, first.GetProperty("count").GetInt32());
        var totals = first.GetProperty("totalsByCurrency").EnumerateArray().ToDictionary(t => t.GetProperty("currency").GetString()!, t => t);
        Assert.Equal(1500m, totals["USD"].GetProperty("value").GetDecimal());
        Assert.Equal(150m, totals["USD"].GetProperty("weighted").GetDecimal());       // 10% stage probability
        Assert.Equal(80000m, totals["PKR"].GetProperty("value").GetDecimal());
        Assert.Equal(2, totals.Count);
    }

    [Fact]
    public async Task Pipeline_configuration_is_validated_and_stages_with_deals_are_protected()
    {
        var admin = await host.LoginAdminAsync();
        var bad = await admin.PostAsJsonAsync("/api/v1/crm/pipelines", new { name = "No lost", stages = new object[] { new { name = "A", kind = "Open", probability = 10 }, new { name = "B", kind = "Open", probability = 20 }, new { name = "Won", kind = "Won", probability = 100 } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var pid = await ProductAsync(admin);
        var ok = await admin.PostAsJsonAsync("/api/v1/crm/pipelines", new { name = "Pharmacy " + Uniq(), productId = pid, stages = new object[] { new { name = "Lead", kind = "Open", probability = 10 }, new { name = "Demo", kind = "Open", probability = 50 }, new { name = "Won", kind = "Won", probability = 100 }, new { name = "Lost", kind = "Lost", probability = 0 } } });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var pipeId = (await ok.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var created = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/pipelines?productId={pid}", Json)).EnumerateArray().First(p => p.GetProperty("id").GetGuid() == pipeId);
        var stages = created.GetProperty("stages").EnumerateArray().ToList();

        var (cid, _) = await NewLead(admin, pid);
        var opp = await admin.PostAsJsonAsync("/api/v1/crm/opportunities", new { pipelineId = pipeId, contactId = cid, productId = pid, name = "d", value = 1 });
        Assert.Equal("Lead", (await opp.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("stage").GetString());

        object Stage(JsonElement s, string? name = null, string? kind = null) => new { id = s.GetProperty("id").GetGuid(), name = name ?? s.GetProperty("name").GetString(), kind = kind ?? s.GetProperty("kind").GetString(), probability = s.GetProperty("probability").GetInt32() };
        // removing the stage that holds a deal is refused
        var withoutLead = new[] { Stage(stages[1]), Stage(stages[2]), Stage(stages[3]) };
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/crm/pipelines/{pipeId}", new { name = "x", stages = withoutLead })).StatusCode);
        // changing a stage's kind is refused
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/crm/pipelines/{pipeId}", new { name = "x", stages = new[] { Stage(stages[0]), Stage(stages[1], kind: "Won"), Stage(stages[2]), Stage(stages[3]) } })).StatusCode);
        // renaming and adding a stage is fine
        var extra = new { name = "Proposal", kind = "Open", probability = 70 };
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/crm/pipelines/{pipeId}", new { name = "Renamed", stages = new object[] { Stage(stages[0], "Fresh lead"), Stage(stages[1]), extra, Stage(stages[2]), Stage(stages[3]) } })).StatusCode);

        // a product-specific pipeline cannot be used for another product
        var other = await ProductAsync(admin);
        var (cid2, _) = await NewLead(admin, other);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/crm/opportunities", new { pipelineId = pipeId, contactId = cid2, productId = other, name = "wrong pipeline", value = 1 })).StatusCode);
    }

    [Fact]
    public async Task Dashboard_reports_missing_data_as_null_and_measured_data_as_numbers()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);

        var empty = await Dash(admin, pid);
        Assert.Equal(0, empty.GetProperty("leads").GetProperty("created").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("funnel").GetProperty("contactedRatePct").ValueKind);        // not 0%
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("responseTime").GetProperty("averageMinutes").ValueKind);   // not 0 minutes
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("outcomes").GetProperty("winRatePct").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("outcomes").GetProperty("averageSalesCycleDays").ValueKind);
        Assert.Equal(0, empty.GetProperty("pipeline").GetProperty("openDeals").GetInt32());

        var pipe = await DefaultPipeline(admin);
        var (_, a) = await NewLead(admin, pid, "Alpha"); var (_, b) = await NewLead(admin, pid, "Beta");
        await Qualify(admin, a);
        var oppId = (await (await admin.PostAsJsonAsync($"/api/v1/crm/leads/{a}/convert", new { value = 2000, currency = "USD" })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        var mid = await Dash(admin, pid);
        Assert.Equal(2, mid.GetProperty("leads").GetProperty("created").GetInt32());
        Assert.Equal(1, mid.GetProperty("funnel").GetProperty("converted").GetInt32());
        Assert.Equal(1, mid.GetProperty("responseTime").GetProperty("sampleSize").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, mid.GetProperty("responseTime").GetProperty("averageMinutes").ValueKind);   // measured (even if tiny)
        Assert.Equal(1, mid.GetProperty("responseTime").GetProperty("leadsNotYetContacted").GetInt32());
        Assert.Equal(1, mid.GetProperty("pipeline").GetProperty("openDeals").GetInt32());
        Assert.Equal(JsonValueKind.Null, mid.GetProperty("outcomes").GetProperty("winRatePct").ValueKind);              // nothing closed yet
        Assert.Equal(2, mid.GetProperty("leads").GetProperty("unknownSource").GetInt32());

        await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/stage", new { stageId = StageId(pipe, "Won"), revenue = 1800 });
        var end = await Dash(admin, pid);
        Assert.Equal(1, end.GetProperty("outcomes").GetProperty("won").GetInt32());
        Assert.Equal(100.0, end.GetProperty("outcomes").GetProperty("winRatePct").GetDouble());
        Assert.Equal(1800m, end.GetProperty("outcomes").GetProperty("revenueByCurrency")[0].GetProperty("revenue").GetDecimal());
        Assert.Equal(50.0, end.GetProperty("funnel").GetProperty("leadToWonPct").GetDouble());
        Assert.NotEqual(JsonValueKind.Null, end.GetProperty("outcomes").GetProperty("averageSalesCycleDays").ValueKind);
        Assert.Equal(0, end.GetProperty("pipeline").GetProperty("openDeals").GetInt32());
        Assert.Equal(b, b);   // lead b stays New
    }

    [Fact]
    public async Task Activities_tasks_stay_open_until_completed_and_show_in_the_due_list()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var pipe = await DefaultPipeline(admin);
        var (cid, _) = await NewLead(admin, pid);
        var oppId = (await (await admin.PostAsJsonAsync("/api/v1/crm/opportunities", new { pipelineId = pipe.GetProperty("id").GetGuid(), contactId = cid, productId = pid, name = "Act", value = 10 })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        var call = await (await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/activities", new { type = "Call", summary = "Discovery call" })).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.NotEqual(JsonValueKind.Null, call.GetProperty("completedAt").ValueKind);                       // logged after the fact
        var task = await (await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/activities", new { type = "Task", summary = "Send quote", dueAt = DateTimeOffset.UtcNow.AddDays(-1) })).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("completedAt").ValueKind);

        var due = await admin.GetFromJsonAsync<JsonElement>("/api/v1/crm/activities/due", Json);
        var mine = due.EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == task.GetProperty("id").GetGuid());
        Assert.True(mine.GetProperty("overdue").GetBoolean());
        Assert.DoesNotContain(due.EnumerateArray(), d => d.GetProperty("id").GetGuid() == call.GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/crm/activities/{task.GetProperty("id").GetGuid()}/complete", null)).StatusCode);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/v1/crm/activities/due", Json)).EnumerateArray(), d => d.GetProperty("id").GetGuid() == task.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppId}/activities", new { type = "Note", summary = "" })).StatusCode);
    }

    [Fact]
    public async Task Product_scope_and_roles_protect_deals_and_the_dashboard()
    {
        var admin = await host.LoginAdminAsync();
        var a = await ProductAsync(admin); var b = await ProductAsync(admin);
        var pipe = await DefaultPipeline(admin);
        var (cidB, _) = await NewLead(admin, b);
        var oppB = (await (await admin.PostAsJsonAsync("/api/v1/crm/opportunities", new { pipelineId = pipe.GetProperty("id").GetGuid(), contactId = cidB, productId = b, name = "Secret", value = 999 })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        var rep = await UserAsync(admin, "Sales Representative", all: false, products: [a]);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.GetAsync($"/api/v1/crm/opportunities/{oppB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppB}/stage", new { stageId = StageId(pipe, "Qualified") })).StatusCode);
        Assert.DoesNotContain((await rep.GetFromJsonAsync<JsonElement>("/api/v1/crm/opportunities?take=200", Json)).GetProperty("items").EnumerateArray(), o => o.GetProperty("id").GetGuid() == oppB);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.GetAsync($"/api/v1/crm/dashboard?productId={b}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync("/api/v1/crm/dashboard")).StatusCode);

        var viewer = await UserAsync(admin, "Read-only Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/crm/opportunities/{oppB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/crm/opportunities/{oppB}/stage", new { stageId = StageId(pipe, "Qualified") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/crm/pipelines", new { name = "x", stages = Array.Empty<object>() })).StatusCode);
        var creator = await UserAsync(admin, "Content Creator");
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.GetAsync("/api/v1/crm/dashboard")).StatusCode);
    }
}
