using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class LeadTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private const string ConsentText = "Yes, email me about Demo POS offers.";

    private async Task<(HttpClient Client, Guid Id)> UserAsync(HttpClient admin, string role, bool all = true, Guid[]? products = null)
    {
        var email = $"lead-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "U " + Uniq(), password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = all, productIds = products })).EnsureSuccessStatusCode();
        var c = await host.LoginAsync(email, "Some-Strong-Pass-123");
        return (c, (await c.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Json)).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Lead " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Id, string Key)> FormAsync(HttpClient admin, Guid productId, Guid[]? pool = null, string? consent = ConsentText)
    {
        var r = await admin.PostAsJsonAsync($"/api/v1/crm/products/{productId}/lead-forms", new { name = "Demo form " + Uniq(), consentText = consent, ownerPool = pool });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (j.GetProperty("id").GetGuid(), j.GetProperty("publicKey").GetString()!);
    }

    /// <summary>Anonymous visitor: no cookie, no CSRF header.</summary>
    private HttpResponseMessage Submit(string key, object body) => host.CreateClient().PostAsJsonAsync($"/api/v1/public/lead-forms/{key}/submit", body).Result;

    private static async Task<JsonElement> Leads(HttpClient c, string qs) => (await c.GetFromJsonAsync<JsonElement>("/api/v1/crm/leads?" + qs, Json)).GetProperty("items");
    private static async Task<JsonElement?> LeadFor(HttpClient c, Guid productId, string contactName)
        => (await Leads(c, $"productId={productId}&take=200")).EnumerateArray().Cast<JsonElement?>().FirstOrDefault(l => l!.Value.GetProperty("contactName").GetString() == contactName);

    [Fact]
    public async Task Anonymous_form_creates_contact_and_lead_stores_the_exact_consent_text_and_round_robins_owners()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var (_, rep1) = await UserAsync(admin, "Sales Representative");
        var (_, rep2) = await UserAsync(admin, "Sales Representative");
        var (_, key) = await FormAsync(admin, pid, [rep1, rep2]);

        var e1 = $"a{Uniq()}@example.com"; var e2 = $"b{Uniq()}@example.com"; var e3 = $"c{Uniq()}@example.com";
        Assert.Equal(HttpStatusCode.Accepted, Submit(key, new { firstName = "Ann", lastName = "One", email = e1, company = "Ann's Pharmacy", message = "Please book a demo", consent = true }).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, Submit(key, new { firstName = "Bo", lastName = "Two", email = e2, consent = false }).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, Submit(key, new { firstName = "Cy", lastName = "Three", email = e3 }).StatusCode);

        var leads = await Leads(admin, $"productId={pid}");
        Assert.Equal(3, leads.GetArrayLength());
        var owners = leads.EnumerateArray().OrderBy(l => l.GetProperty("createdAt").GetDateTimeOffset()).Select(l => l.GetProperty("ownerUserId").GetGuid()).ToArray();
        Assert.Equal([rep1, rep2, rep1], owners);                                        // round robin

        // consent: only the person who ticked the box, with the exact wording and the form it came from
        var ann = await LeadFor(admin, pid, "Ann One");
        var annContact = ann!.Value.GetProperty("contactId").GetGuid();
        var consent = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{annContact}/consent", Json);
        Assert.Equal(1, consent.GetArrayLength());
        Assert.Contains(ConsentText, consent[0].GetProperty("evidence").GetString());
        Assert.StartsWith("lead form:", consent[0].GetProperty("source").GetString());

        var boContact = (await LeadFor(admin, pid, "Bo Two"))!.Value.GetProperty("contactId").GetGuid();
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{boContact}/consent", Json)).GetArrayLength());
        var can = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{boContact}/can-contact?channel=Email&purpose=Marketing", Json);
        Assert.False(can.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Consent_cannot_be_forged_when_the_form_does_not_ask_for_it()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var (_, key) = await FormAsync(admin, pid, consent: null);
        Assert.Equal(HttpStatusCode.Accepted, Submit(key, new { firstName = "Eve", lastName = "Forge", email = $"e{Uniq()}@example.com", consent = true }).StatusCode);
        var contact = (await LeadFor(admin, pid, "Eve Forge"))!.Value.GetProperty("contactId").GetGuid();
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{contact}/consent", Json)).GetArrayLength());
    }

    [Fact]
    public async Task Repeat_enquiries_attach_to_the_open_lead_and_bots_and_bad_input_are_handled()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var (_, key) = await FormAsync(admin, pid);
        var email = $"r{Uniq()}@example.com";

        Assert.Equal(HttpStatusCode.Accepted, Submit(key, new { firstName = "Rae", lastName = "Repeat", email, message = "first" }).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, Submit(key, new { firstName = "Rae", lastName = "Repeat", email = email.ToUpperInvariant(), message = "second" }).StatusCode);
        Assert.Equal(1, (await Leads(admin, $"productId={pid}")).GetArrayLength());       // one lead, not two
        var contact = (await LeadFor(admin, pid, "Rae Repeat"))!.Value.GetProperty("contactId").GetGuid();
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{contact}", Json);
        Assert.Contains(detail.GetProperty("interactions").EnumerateArray(), i => i.GetProperty("summary").GetString()!.Contains("Enquired again"));

        // honeypot: accepted silently, nothing stored
        Assert.Equal(HttpStatusCode.Accepted, Submit(key, new { firstName = "Bot", email = $"bot{Uniq()}@example.com", website = "http://spam.example" }).StatusCode);
        Assert.Equal(1, (await Leads(admin, $"productId={pid}")).GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, Submit(key, new { firstName = "X", email = "nope" }).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, Submit(key, new { firstName = "No contact method" }).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, Submit(key, new { firstName = new string('x', 400), email = $"l{Uniq()}@example.com" }).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, Submit("not-a-real-key", new { email = "a@b.co" }).StatusCode);

        // disabled forms stop accepting
        var formId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/products/{pid}/lead-forms", Json)).EnumerateArray().First().GetProperty("id").GetGuid();
        await admin.PutAsJsonAsync($"/api/v1/crm/lead-forms/{formId}", new { name = "Off", enabled = false, consentText = ConsentText });
        Assert.Equal(HttpStatusCode.NotFound, Submit(key, new { email = $"z{Uniq()}@example.com" }).StatusCode);
    }

    [Fact]
    public async Task Public_form_is_rate_limited_per_form()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var (_, key) = await FormAsync(admin, pid);
        var statuses = Enumerable.Range(0, 30).Select(i => Submit(key, new { email = $"rl{i}-{Uniq()}@example.com" }).StatusCode).ToList();
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(20, statuses.Count(s => s == HttpStatusCode.Accepted));

        var (_, other) = await FormAsync(admin, pid);   // a different form has its own budget
        Assert.Equal(HttpStatusCode.Accepted, Submit(other, new { email = $"ok{Uniq()}@example.com" }).StatusCode);
    }

    [Fact]
    public async Task Scoring_rules_are_explainable_and_apply_to_new_leads()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var (_, key) = await FormAsync(admin, pid);
        var tag = Uniq();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/crm/score-rules", new { productId = pid, name = "Has a company " + tag, field = "HasCompany", points = 25 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/crm/score-rules", new { productId = pid, name = "Asked for a demo " + tag, field = "MessageContains", value = "demo", points = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/crm/score-rules", new { name = "no value", field = "MessageContains", points = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/crm/score-rules", new { name = "too big", field = "HasPhone", points = 90 })).StatusCode);

        Submit(key, new { firstName = "Sco", lastName = "Re", email = $"s{Uniq()}@example.com", company = "Big Pharmacy " + tag, message = "We want a DEMO" });
        Submit(key, new { firstName = "Low", lastName = "Score", email = $"l{Uniq()}@example.com" });
        var hi = (await LeadFor(admin, pid, "Sco Re"))!.Value; var lo = (await LeadFor(admin, pid, "Low Score"))!.Value;
        Assert.Equal(55, hi.GetProperty("score").GetInt32());
        Assert.Equal(0, lo.GetProperty("score").GetInt32());

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/leads/{hi.GetProperty("id").GetGuid()}", Json);
        Assert.Equal(2, detail.GetProperty("scoreBreakdown").GetArrayLength());
    }

    [Fact]
    public async Task Lead_workflow_assignment_follow_up_and_guard_rails()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var (rep, repId) = await UserAsync(admin, "Sales Representative");
        var (_, key) = await FormAsync(admin, pid);
        Submit(key, new { firstName = "Wo", lastName = "Rk", email = $"w{Uniq()}@example.com" });
        var lead = (await LeadFor(admin, pid, "Wo Rk"))!.Value; var id = lead.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/crm/leads/{id}/assign", new { userId = repId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/crm/leads/{id}/assign", new { userId = Guid.NewGuid() })).StatusCode);   // unknown user
        Assert.Equal(1, (await Leads(admin, $"productId={pid}&ownerId={repId}")).GetArrayLength());

        // cannot jump straight to qualified, cannot qualify without a note, cannot convert by status
        Assert.Equal(HttpStatusCode.Conflict, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "Qualified", note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "Contacted" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "Qualified" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "Converted" })).StatusCode);

        // follow-up due in the past shows as overdue
        Assert.Equal(HttpStatusCode.NoContent, (await rep.PutAsJsonAsync($"/api/v1/crm/leads/{id}/next-action", new { text = "Call back", due = DateTimeOffset.UtcNow.AddHours(-2) })).StatusCode);
        Assert.Equal(1, (await Leads(rep, $"productId={pid}&overdue=true")).GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "Qualified", note = "Owns 3 shops, wants POS this quarter" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "Disqualified" })).StatusCode);   // reason required... and not valid without one
        Assert.Equal(HttpStatusCode.OK, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "Disqualified", reason = "Went with a competitor" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rep.PutAsJsonAsync($"/api/v1/crm/leads/{id}/next-action", new { text = "x" })).StatusCode);            // closed leads have no next action
        Assert.Equal(HttpStatusCode.OK, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{id}/status", new { status = "New" })).StatusCode);                     // reopen

        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?subjectType=Lead&take=100", Json);
        var actions = audit.GetProperty("items").EnumerateArray().Where(a => a.GetProperty("subjectId").GetString() == id.ToString()).Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("crm.lead.assigned", actions); Assert.Contains("crm.lead.status-changed", actions);
    }

    [Fact]
    public async Task Csv_import_dry_run_writes_nothing_real_run_creates_and_never_assumes_consent()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var tag = Uniq();
        var csv = new StringBuilder("firstName,lastName,email,phone,company,source,consent,consentEvidence\n");
        csv.AppendLine($"Ivy,One,ivy{tag}@example.com,+92 300 1112222,Ivy Shop,trade-show,yes,\"Signed sheet at the booth, 14 Nov\"");
        csv.AppendLine($"Jon,Two,jon{tag}@example.com,,,trade-show,no,");
        csv.AppendLine($"Bad,Email,not-an-email,,,,,");
        csv.AppendLine($"Lia,NoEvidence,lia{tag}@example.com,,,,yes,");
        csv.AppendLine($"Kim,Three,kim{tag}@example.com,,,,,");

        async Task<JsonElement> Import(bool dry, string content, string name = "leads.csv")
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name } };
            var r = await admin.PostAsync($"/api/v1/crm/products/{pid}/leads/import?dryRun={dry.ToString().ToLowerInvariant()}", form);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        }

        var dry = await Import(true, csv.ToString());
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(3, dry.GetProperty("leadsCreated").GetInt32());
        Assert.Equal(2, dry.GetProperty("errors").GetArrayLength());
        Assert.Equal(0, (await Leads(admin, $"productId={pid}")).GetArrayLength());       // nothing written

        var real = await Import(false, csv.ToString());
        Assert.Equal(3, real.GetProperty("contactsCreated").GetInt32());
        Assert.Equal(3, (await Leads(admin, $"productId={pid}")).GetArrayLength());
        var rows = real.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("row").GetInt32()).OrderBy(x => x).ToArray();
        Assert.Equal([4, 5], rows);                                                          // CSV row numbers (header = 1)

        var ivy = (await LeadFor(admin, pid, "Ivy One"))!.Value.GetProperty("contactId").GetGuid();
        var jon = (await LeadFor(admin, pid, "Jon Two"))!.Value.GetProperty("contactId").GetGuid();
        Assert.Equal(1, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{ivy}/consent", Json)).GetArrayLength());
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{jon}/consent", Json)).GetArrayLength());   // consent=no => none recorded

        var again = await Import(false, csv.ToString());                                     // re-import: matched, attached, no duplicates
        Assert.Equal(0, again.GetProperty("leadsCreated").GetInt32());
        Assert.Equal(3, again.GetProperty("attachedToOpenLead").GetInt32());
        Assert.Equal(3, (await Leads(admin, $"productId={pid}")).GetArrayLength());

        var notCsv = await admin.PostAsync($"/api/v1/crm/products/{pid}/leads/import", new MultipartFormDataContent { { new ByteArrayContent("x"u8.ToArray()), "file", "leads.exe" } });
        Assert.Equal(HttpStatusCode.BadRequest, notCsv.StatusCode);
    }

    [Fact]
    public async Task Leads_respect_product_scope_and_roles()
    {
        var admin = await host.LoginAdminAsync();
        var a = await ProductAsync(admin); var b = await ProductAsync(admin);
        var (_, keyA) = await FormAsync(admin, a); var (_, keyB) = await FormAsync(admin, b);
        Submit(keyA, new { firstName = "Only", lastName = "InA", email = $"a{Uniq()}@example.com" });
        Submit(keyB, new { firstName = "Only", lastName = "InB", email = $"b{Uniq()}@example.com" });
        var inB = (await LeadFor(admin, b, "Only InB"))!.Value.GetProperty("id").GetGuid();

        var (rep, _) = await UserAsync(admin, "Sales Representative", all: false, products: [a]);
        var visible = (await Leads(rep, "take=200")).EnumerateArray().Select(l => l.GetProperty("contactName").GetString()).ToList();
        Assert.Contains("Only InA", visible); Assert.DoesNotContain("Only InB", visible);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.GetAsync($"/api/v1/crm/leads/{inB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.PostAsJsonAsync($"/api/v1/crm/leads/{inB}/status", new { status = "Contacted" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.GetAsync($"/api/v1/crm/products/{b}/lead-forms")).StatusCode);

        var (viewer, _) = await UserAsync(admin, "Read-only Viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/crm/leads/{inB}/status", new { status = "Contacted" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/crm/score-rules", new { name = "x", field = "HasPhone", points = 5 })).StatusCode);

        var dir = await rep.GetFromJsonAsync<JsonElement>("/api/v1/directory/users", Json);
        Assert.True(dir.GetArrayLength() > 1);
    }
}
