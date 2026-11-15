using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class PortfolioApprovalTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object ValidContent(string summary = "Pharmacy point of sale") => new
    {
        summary,
        facts = new[] { new { id = "", category = "Feature", text = "Works offline", evidence = "Spec v3", verified = true } },
        claims = Array.Empty<object>(),
    };

    private async Task<HttpClient> UserAsync(HttpClient admin, string role, bool allProducts = true, Guid[]? products = null)
    {
        var email = $"{role.Replace(' ', '-').ToLowerInvariant()}-{Guid.NewGuid():N}@test.local";
        var r = await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts, productIds = products });
        r.EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Json);

    private static async Task<Guid> CreateProduct(HttpClient admin, string name = "Pharmacy POS")
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Pharmacy POS").GetProperty("id").GetGuid();
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = name + " " + Guid.NewGuid().ToString("N")[..6], categoryId = catId, identifiers = new Dictionary<string, string> { ["market"] = "PK" } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PendingApprovalFor(HttpClient c, Guid productId)
    {
        var list = await c.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={productId}", Json);
        return list.EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Full_profile_lifecycle_with_four_eyes_and_immutability()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await CreateProduct(admin);

        // seeded category list, slug generated
        var prod = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}", Json);
        Assert.StartsWith("pharmacy-pos-", prod.GetProperty("slug").GetString());

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null)).StatusCode);  // one open version

        // incomplete content cannot be submitted
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new { content = new { summary = "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null)).StatusCode);

        // complete it and submit
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new { content = ValidContent(), changeNote = "first" })).StatusCode);
        var submit = await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal("InReview", (await Body(submit)).GetProperty("status").GetString());

        // in review => immutable
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new { content = ValidContent("changed") })).StatusCode);

        // requester cannot approve own request (four-eyes)
        var approvalId = await PendingApprovalFor(admin, pid);
        var self = await admin.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "Approved" });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);

        // a Content Creator lacks the permission
        var creator = await UserAsync(admin, "Content Creator");
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "Approved" })).StatusCode);

        // Approver approves
        var approver = await UserAsync(admin, "Approver");
        var ok = await approver.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "Approved", comment = "looks right" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var approved = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/profile/approved", Json);
        Assert.Equal(1, approved.GetProperty("number").GetInt32());
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(1, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}", Json)).GetProperty("approvedProfileVersion").GetInt32());

        // approved => immutable; deciding twice => 409
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new { content = ValidContent("tamper") })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "Approved" })).StatusCode);

        // v2 clones v1, supersedes it once approved
        var v2 = await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        Assert.Equal(HttpStatusCode.Created, v2.StatusCode);
        Assert.Equal("Works offline", (await Body(v2)).GetProperty("content").GetProperty("facts")[0].GetProperty("text").GetString());
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/2/submit", null);
        var a2 = await PendingApprovalFor(admin, pid);
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/api/v1/approvals/{a2}/decision", new { decision = "Approved" })).StatusCode);
        var versions = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/profile/versions", Json);
        Assert.Equal("Superseded", versions.EnumerateArray().Single(v => v.GetProperty("number").GetInt32() == 1).GetProperty("status").GetString());
        Assert.Equal(2, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}", Json)).GetProperty("approvedProfileVersion").GetInt32());

        // everything above is audited
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={pid}&take=100", Json);
        var actions = audit.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("approval.requested", actions);
        Assert.Contains("approval.approved", actions);
        Assert.Contains("product.created", actions);
    }

    [Fact]
    public async Task Reject_and_changes_requested_need_a_comment_and_changes_return_to_draft()
    {
        var admin = await host.LoginAdminAsync();
        var approver = await UserAsync(admin, "Approver");
        var pid = await CreateProduct(admin);
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new { content = ValidContent() });
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null);
        var id = await PendingApprovalFor(admin, pid);

        Assert.Equal(HttpStatusCode.BadRequest, (await approver.PostAsJsonAsync($"/api/v1/approvals/{id}/decision", new { decision = "Rejected" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/api/v1/approvals/{id}/decision", new { decision = "ChangesRequested", comment = "add pricing" })).StatusCode);

        var v = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/profile/versions/1", Json);
        Assert.Equal("Draft", v.GetProperty("status").GetString());
        Assert.Equal("add pricing", v.GetProperty("decisionComment").GetString());
        // editable again
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new { content = ValidContent("fixed") })).StatusCode);
    }

    [Fact]
    public async Task Withdrawn_request_cannot_be_decided()
    {
        var admin = await host.LoginAdminAsync();
        var approver = await UserAsync(admin, "Approver");
        var pid = await CreateProduct(admin);
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new { content = ValidContent() });
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null);
        var id = await PendingApprovalFor(admin, pid);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/withdraw", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync($"/api/v1/approvals/{id}/decision", new { decision = "Approved" })).StatusCode);
    }

    [Fact]
    public async Task Product_scope_hides_other_products_and_blocks_creation()
    {
        var admin = await host.LoginAdminAsync();
        var a = await CreateProduct(admin, "Scope A");
        var b = await CreateProduct(admin, "Scope B");
        var scoped = await UserAsync(admin, "Product Manager", allProducts: false, products: [a]);

        Assert.Equal(HttpStatusCode.OK, (await scoped.GetAsync($"/api/v1/products/{a}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/v1/products/{b}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.PostAsync($"/api/v1/products/{b}/profile/versions", null)).StatusCode);
        var list = await scoped.GetFromJsonAsync<JsonElement>("/api/v1/products", Json);
        Assert.Equal([a], list.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray());

        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.PostAsJsonAsync("/api/v1/products", new { name = "Nope", categoryId = catId })).StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_write()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await CreateProduct(admin);
        var viewer = await UserAsync(admin, "Read-only Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/products/{pid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync($"/api/v1/products/{pid}/profile/versions", null)).StatusCode);
        // Write endpoints answer 404 (not 403) when the caller lacks the write permission, so existence is not leaked.
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PutAsJsonAsync($"/api/v1/products/{pid}", new { name = "x" })).StatusCode);
    }
}
