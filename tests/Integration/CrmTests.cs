using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class CrmTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> UserAsync(HttpClient admin, string role, bool all = true, Guid[]? products = null)
    {
        var email = $"crm-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = all, productIds = products })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Crm " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> NewContact(HttpClient c, object body)
    {
        var r = await c.PostAsJsonAsync("/api/v1/crm/contacts", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<(bool Allowed, string Reason)> Can(HttpClient c, Guid id, string channel = "Email", string purpose = "Marketing")
    {
        var j = await c.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{id}/can-contact?channel={channel}&purpose={purpose}", Json);
        return (j.GetProperty("allowed").GetBoolean(), j.GetProperty("reason").GetString()!);
    }

    private static object Consent(string status = "Granted", string channel = "Email", string purpose = "Marketing", string? evidence = "Ticked the box on the demo form")
        => new { channel, purpose, status, source = "web form", evidence };

    [Fact]
    public async Task Contacts_are_deduplicated_by_normalized_address()
    {
        var admin = await host.LoginAdminAsync();
        var e = $"Dup.{Uniq()}@Example.com";
        var id = await NewContact(admin, new { firstName = "Dana", lastName = "Dup", methods = new[] { new { type = "Email", value = e, isPrimary = true } } });

        var again = await admin.PostAsJsonAsync("/api/v1/crm/contacts", new { firstName = "Other", methods = new[] { new { type = "Email", value = "  " + e.ToLowerInvariant() + " " } } });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(id, body.GetProperty("existingContactId").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/crm/contacts", new { firstName = "Bad", methods = new[] { new { type = "Email", value = "nope" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/crm/contacts", new { })).StatusCode);
    }

    [Fact]
    public async Task Marketing_requires_recorded_evidence_backed_consent_and_withdrawal_stops_it()
    {
        var admin = await host.LoginAdminAsync();
        var id = await NewContact(admin, new { firstName = "Con", lastName = "Sent", methods = new[] { new { type = "Email", value = $"c{Uniq()}@example.com" } } });

        Assert.False((await Can(admin, id)).Allowed);                                  // nothing recorded
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{id}/consent", Consent(evidence: null))).StatusCode);   // grant needs evidence
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{id}/consent", new { channel = "Email", purpose = "Marketing", status = "Granted", source = "", evidence = "x" })).StatusCode);
        Assert.False((await Can(admin, id)).Allowed);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{id}/consent", Consent())).StatusCode);
        Assert.True((await Can(admin, id)).Allowed);
        Assert.False((await Can(admin, id, "WhatsApp")).Allowed);                       // different channel: no address, no consent

        await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{id}/consent", new { channel = "Email", purpose = "Marketing", status = "Withdrawn", source = "unsubscribe link" });
        var after = await Can(admin, id);
        Assert.False(after.Allowed);
        Assert.Contains("withdrawn", after.Reason, StringComparison.OrdinalIgnoreCase);

        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{id}/consent", Json);
        Assert.Equal(2, history.GetArrayLength());                                      // append-only history
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/governance/audit?subjectType=Contact&take=100", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("subjectId").GetString() == id.ToString() && a.GetProperty("action").GetString() == "crm.consent.withdrawn");
    }

    [Fact]
    public async Task Transactional_messages_need_no_marketing_consent_but_respect_opt_out_suppression_and_the_pause_switch()
    {
        var admin = await host.LoginAdminAsync();
        var email = $"t{Uniq()}@example.com";
        var id = await NewContact(admin, new { firstName = "Tran", methods = new[] { new { type = "Email", value = email } } });

        Assert.True((await Can(admin, id, purpose: "Transactional")).Allowed);
        Assert.False((await Can(admin, id, purpose: "Marketing")).Allowed);

        // address-level suppression blocks everything on that channel
        await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{id}/consent", Consent());
        Assert.True((await Can(admin, id)).Allowed);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/crm/suppressions", new { channel = "Email", value = email.ToUpperInvariant(), reason = "hard bounce" })).StatusCode);
        Assert.Contains("suppressed", (await Can(admin, id)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False((await Can(admin, id, purpose: "Transactional")).Allowed);

        // emergency stop
        var id2 = await NewContact(admin, new { firstName = "Paused", methods = new[] { new { type = "Email", value = $"p{Uniq()}@example.com" } } });
        Assert.True((await Can(admin, id2, purpose: "Transactional")).Allowed);
        await admin.PutAsJsonAsync("/api/v1/governance/pause/Messaging", new { isPaused = true, reason = "test" });
        try { Assert.Contains("paused", (await Can(admin, id2, purpose: "Transactional")).Reason, StringComparison.OrdinalIgnoreCase); }
        finally { await admin.PutAsJsonAsync("/api/v1/governance/pause/Messaging", new { isPaused = false }); }
        Assert.True((await Can(admin, id2, purpose: "Transactional")).Allowed);
    }

    [Fact]
    public async Task Merge_moves_everything_keeps_consent_history_and_leaves_a_tombstone()
    {
        var admin = await host.LoginAdminAsync();
        var p1 = await ProductAsync(admin); var p2 = await ProductAsync(admin);
        var tag = "vip" + Uniq();
        var survivor = await NewContact(admin, new { firstName = "Sam", lastName = "Survivor", methods = new[] { new { type = "Email", value = $"s{Uniq()}@example.com", isPrimary = true } }, tags = new[] { tag }, associations = new[] { new { productId = p1 } }, consents = new[] { Consent() } });
        await Task.Delay(30);   // the later withdrawal below must have a strictly later timestamp
        var srcEmail = $"x{Uniq()}@example.com";
        var source = await NewContact(admin, new { firstName = "Sam", lastName = "Source", methods = new[] { new { type = "Email", value = srcEmail, isPrimary = true }, new { type = "Phone", value = "+92 300 5550000", isPrimary = false } }, tags = new[] { tag, "extra" + Uniq() }, associations = new[] { new { productId = p1 }, new { productId = p2 } }, notes = "called twice" });
        await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{source}/consent", new { channel = "Email", purpose = "Marketing", status = "Withdrawn", source = "reply STOP" });
        await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{source}/interactions", new { type = "Note", summary = "asked for a demo" });

        var merge = await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{survivor}/merge", new { sourceId = source });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);

        var d = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{survivor}", Json);
        Assert.Equal(3, d.GetProperty("methods").GetArrayLength());
        Assert.Equal(1, d.GetProperty("methods").EnumerateArray().Count(m => m.GetProperty("type").GetString() == "Email" && m.GetProperty("isPrimary").GetBoolean()));   // one primary per type
        Assert.Equal(2, d.GetProperty("associations").GetArrayLength());                 // p1 not duplicated, p2 added
        Assert.Equal(2, d.GetProperty("tags").EnumerateArray().Count(t => t.GetString()!.StartsWith("extra") || t.GetString() == tag));
        Assert.Contains("called twice", d.GetProperty("contact").GetProperty("notes").GetString());
        Assert.Contains(d.GetProperty("interactions").EnumerateArray(), i => i.GetProperty("summary").GetString() == "asked for a demo");

        // the source's later withdrawal beats the survivor's earlier grant: merging must never re-enable messaging
        Assert.False((await Can(admin, survivor)).Allowed);
        Assert.Equal(HttpStatusCode.Gone, (await admin.GetAsync($"/api/v1/crm/contacts/{source}")).StatusCode);
        using (var scope = host.Services.CreateScope())   // automation calls the policy directly, not the endpoint
        {
            var policy = scope.ServiceProvider.GetRequiredService<TriSoftic.GrowthEngine.BuildingBlocks.Abstractions.IContactPolicy>();
            var d2 = await policy.CanContactAsync(source, TriSoftic.GrowthEngine.BuildingBlocks.Abstractions.ContactChannel.Email, TriSoftic.GrowthEngine.BuildingBlocks.Abstractions.ContactPurpose.Transactional);
            Assert.False(d2.Allowed);
            Assert.Contains("merged", d2.Reason);
        }
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts?q={Uri.EscapeDataString(srcEmail)}", Json);
        Assert.Equal(survivor, list.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{survivor}/merge", new { sourceId = source })).StatusCode);   // already merged
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{survivor}/merge", new { sourceId = survivor })).StatusCode);
    }

    [Fact]
    public async Task Duplicate_suggestions_find_same_name_at_same_company_and_similar_emails()
    {
        var admin = await host.LoginAdminAsync();
        var co = await admin.PostAsJsonAsync("/api/v1/crm/companies", new { name = "Acme " + Uniq(), website = $"https://acme-{Uniq()}.example.com" });
        var companyId = (await co.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var name = "Pat" + Uniq();
        var a = await NewContact(admin, new { firstName = name, lastName = "Jones", companyId, methods = new[] { new { type = "Email", value = $"pat.jones@one-{Uniq()}.com" } } });
        var b = await NewContact(admin, new { firstName = name, lastName = "Jones", companyId, methods = new[] { new { type = "Email", value = $"pat.jones@two-{Uniq()}.com" } } });

        var dups = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/crm/contacts/{a}/duplicates", Json);
        var hit = dups.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == b);
        var reasons = string.Join("|", hit.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("same name at the same company", reasons);

        // duplicate company domain is refused
        var dupCo = await admin.PostAsJsonAsync("/api/v1/crm/companies", new { name = "Again", website = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/crm/companies?q=Acme", Json)).EnumerateArray().First(c => c.GetProperty("id").GetGuid() == companyId).GetProperty("website").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, dupCo.StatusCode);
    }

    [Fact]
    public async Task Search_and_segments_filter_by_text_tag_product_and_consent()
    {
        var admin = await host.LoginAdminAsync();
        var p = await ProductAsync(admin);
        var tag = "seg" + Uniq(); var marker = "Zed" + Uniq();
        var yes = await NewContact(admin, new { firstName = marker, lastName = "Opted", methods = new[] { new { type = "Email", value = $"y{Uniq()}@example.com" } }, tags = new[] { tag }, associations = new[] { new { productId = p } }, consents = new[] { Consent() } });
        var no = await NewContact(admin, new { firstName = marker, lastName = "NoConsent", methods = new[] { new { type = "Email", value = $"n{Uniq()}@example.com" } }, tags = new[] { tag }, associations = new[] { new { productId = p } } });

        async Task<List<Guid>> Ids(string qs) => (await admin.GetFromJsonAsync<JsonElement>("/api/v1/crm/contacts?" + qs, Json)).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();

        Assert.Equivalent(new[] { yes, no }, await Ids($"q={marker}"));
        Assert.Equivalent(new[] { yes, no }, await Ids($"tag={tag}&productId={p}"));
        Assert.Equivalent(new[] { yes }, await Ids($"tag={tag}&consentChannel=Email"));
        Assert.Empty(await Ids($"tag={tag}&productId={Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Product_scoped_users_only_see_contacts_for_their_products_or_their_own_unassociated_ones()
    {
        var admin = await host.LoginAdminAsync();
        var a = await ProductAsync(admin); var b = await ProductAsync(admin);
        var rep = await UserAsync(admin, "Sales Representative", all: false, products: [a]);

        var forA = await NewContact(admin, new { firstName = "ForA", associations = new[] { new { productId = a } } });
        var forB = await NewContact(admin, new { firstName = "ForB", associations = new[] { new { productId = b } } });
        var unassociatedByAdmin = await NewContact(admin, new { firstName = "Loose" });
        var mine = await NewContact(rep, new { firstName = "Mine" });

        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync($"/api/v1/crm/contacts/{forA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.GetAsync($"/api/v1/crm/contacts/{forB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.GetAsync($"/api/v1/crm/contacts/{unassociatedByAdmin}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync($"/api/v1/crm/contacts/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rep.PostAsJsonAsync($"/api/v1/crm/contacts/{forB}/consent", Consent())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rep.PostAsJsonAsync("/api/v1/crm/contacts", new { firstName = "X", associations = new[] { new { productId = b } } })).StatusCode);   // cannot attach to a product they cannot access

        var ids = (await rep.GetFromJsonAsync<JsonElement>("/api/v1/crm/contacts?take=200", Json)).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(forA, ids); Assert.Contains(mine, ids);
        Assert.DoesNotContain(forB, ids); Assert.DoesNotContain(unassociatedByAdmin, ids);
    }

    [Fact]
    public async Task Read_only_roles_cannot_write_and_content_roles_have_no_crm_access()
    {
        var admin = await host.LoginAdminAsync();
        var id = await NewContact(admin, new { firstName = "Perm", methods = new[] { new { type = "Email", value = $"pm{Uniq()}@example.com" } } });
        var viewer = await UserAsync(admin, "Read-only Viewer");
        var creator = await UserAsync(admin, "Content Creator");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/crm/contacts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/crm/contacts", new { firstName = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/crm/contacts/{id}/consent", Consent())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.GetAsync("/api/v1/crm/contacts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/crm/suppressions", new { channel = "Email", value = "a@b.com", reason = "x" })).StatusCode);
    }
}
