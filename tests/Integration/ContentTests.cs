using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class ContentTests(TestHost host)
{
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();   // the server runs on this clock, so "in the future" must be measured on it too
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First().GetProperty("id").GetGuid();
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Cnt " + Guid.NewGuid().ToString("N")[..6], categoryId = catId });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> UserAsync(HttpClient admin, string role, bool all = true, Guid[]? products = null)
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = all, productIds = products })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private static async Task<Guid> NewContent(HttpClient c, Guid pid, string body = "Hello shop owners", object? extra = null)
    {
        var r = await c.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "Post", channel = "facebook", format = "Post", body });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PendingFor(HttpClient c, Guid contentId)
        => (await c.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={contentId}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> Decide(HttpClient c, Guid approvalId, string decision, string? comment = null)
        => c.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision, comment });

    private static async Task<JsonElement> Item(HttpClient c, Guid id) => (await c.GetFromJsonAsync<JsonElement>($"/api/v1/content/{id}", Json)).GetProperty("item");

    [Fact]
    public async Task Review_approve_schedule_and_calendar()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var approver = await UserAsync(admin, "Approver");
        var id = await NewContent(admin, pid);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/content/{id}/submit", null)).StatusCode);
        Assert.Equal("InReview", (await Item(admin, id)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/content/{id}/submit", null)).StatusCode);

        // cannot schedule before approval
        var at = Clock.UtcNow.AddDays(2);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at })).StatusCode);

        var ap = await PendingFor(admin, id);
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver, ap, "Approved")).StatusCode);
        var item = await Item(admin, id);
        Assert.Equal("Approved", item.GetProperty("status").GetString());
        Assert.Equal(1, item.GetProperty("approvedVersion").GetInt32());

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddMinutes(-5) })).StatusCode);   // past
        var sched = await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at });
        Assert.Equal(HttpStatusCode.OK, sched.StatusCode);

        var cal = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?from={Uri.EscapeDataString(at.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(at.AddDays(1).ToString("o"))}&productId={pid}", Json);
        Assert.Equal(id, cal.EnumerateArray().Single().GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/content/{id}/unschedule", null)).StatusCode);
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?from={Uri.EscapeDataString(at.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(at.AddDays(1).ToString("o"))}&productId={pid}", Json)).GetArrayLength());

        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={pid}&take=100", Json);
        var actions = audit.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("content.scheduled", actions); Assert.Contains("approval.approved", actions); Assert.Contains("content.unscheduled", actions);
    }

    [Fact]
    public async Task Editing_scheduled_content_drops_the_schedule_and_the_old_approval_does_not_carry_over()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var approver = await UserAsync(admin, "Approver");
        var id = await NewContent(admin, pid);
        await admin.PostAsync($"/api/v1/content/{id}/submit", null);
        await Decide(approver, await PendingFor(admin, id), "Approved");
        await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddDays(1) });

        var edit = await admin.PostAsJsonAsync($"/api/v1/content/{id}/versions", new { body = "Changed after approval", baseVersion = 1, note = "tweak" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var item = await Item(admin, id);
        Assert.Equal("Draft", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("scheduledFor").ValueKind);
        Assert.Equal(1, item.GetProperty("approvedVersion").GetInt32());     // v1 was approved, v2 is current
        Assert.Equal(2, item.GetProperty("currentVersion").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddDays(1) })).StatusCode);

        // v1 text is preserved in history
        Assert.Equal("Hello shop owners", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{id}/versions/1", Json)).GetProperty("body").GetString());
    }

    [Fact]
    public async Task Editing_while_in_review_withdraws_the_pending_approval_so_the_old_version_cannot_be_approved()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var approver = await UserAsync(admin, "Approver");
        var id = await NewContent(admin, pid);
        await admin.PostAsync($"/api/v1/content/{id}/submit", null);
        var oldApproval = await PendingFor(admin, id);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/versions", new { body = "v2 text", baseVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(approver, oldApproval, "Approved")).StatusCode);    // withdrawn
        Assert.Equal("Draft", (await Item(admin, id)).GetProperty("status").GetString());

        // resubmitting v2 creates a new approval for v2
        await admin.PostAsync($"/api/v1/content/{id}/submit", null);
        var a2 = await PendingFor(admin, id);
        Assert.NotEqual(oldApproval, a2);
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver, a2, "Approved")).StatusCode);
        Assert.Equal(2, (await Item(admin, id)).GetProperty("approvedVersion").GetInt32());
    }

    [Fact]
    public async Task Changes_requested_and_rejection_flow()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var approver = await UserAsync(admin, "Approver");
        var id = await NewContent(admin, pid);
        await admin.PostAsync($"/api/v1/content/{id}/submit", null);
        var ap = await PendingFor(admin, id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Decide(approver, ap, "ChangesRequested")).StatusCode);     // comment required
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver, ap, "ChangesRequested", "shorten it")).StatusCode);
        Assert.Equal("Draft", (await Item(admin, id)).GetProperty("status").GetString());

        await admin.PostAsync($"/api/v1/content/{id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver, await PendingFor(admin, id), "Rejected", "off brand")).StatusCode);
        Assert.Equal("Rejected", (await Item(admin, id)).GetProperty("status").GetString());
        // a rejected item can be revised and submitted again
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/versions", new { body = "better", baseVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/content/{id}/submit", null)).StatusCode);
    }

    [Fact]
    public async Task Stale_edits_are_refused_and_empty_content_cannot_be_submitted()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var id = await NewContent(admin, pid, body: "");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/content/{id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/versions", new { body = "one", baseVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/versions", new { body = "two", baseVersion = 1 })).StatusCode);
    }

    [Fact]
    public async Task Cancelled_content_cannot_be_edited_or_scheduled()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var id = await NewContent(admin, pid);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/content/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/versions", new { body = "x", baseVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/content/{id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Roles_separate_writing_approving_and_scheduling()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var creator = await UserAsync(admin, "Content Creator");
        var approver = await UserAsync(admin, "Approver");
        var manager = await UserAsync(admin, "Marketing Manager");

        var id = await NewContent(creator, pid);
        Assert.Equal(HttpStatusCode.OK, (await creator.PostAsync($"/api/v1/content/{id}/submit", null)).StatusCode);
        var ap = await PendingFor(creator, id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(creator, ap, "Approved")).StatusCode);                  // creators cannot approve
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver, ap, "Approved")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await creator.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddDays(1) })).StatusCode);   // no publishing.manage
        Assert.Equal(HttpStatusCode.NotFound, (await approver.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "x", channel = "fb", body = "y" })).StatusCode);          // approvers do not write
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddDays(1) })).StatusCode);
    }

    [Fact]
    public async Task Product_scope_and_campaign_ownership_are_enforced()
    {
        var admin = await host.LoginAdminAsync();
        var a = await ProductAsync(admin); var b = await ProductAsync(admin);
        var creator = await UserAsync(admin, "Content Creator", all: false, products: [a]);
        var idB = await NewContent(admin, b);

        Assert.Equal(HttpStatusCode.NotFound, (await creator.GetAsync($"/api/v1/content/{idB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await creator.PostAsync($"/api/v1/content/{idB}/submit", null)).StatusCode);
        var visible = await creator.GetFromJsonAsync<JsonElement>("/api/v1/content", Json);
        Assert.DoesNotContain(visible.EnumerateArray(), x => x.GetProperty("id").GetGuid() == idB);

        var camp = await admin.PostAsJsonAsync($"/api/v1/products/{b}/campaigns", new { name = "Launch B" });
        var campId = (await camp.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/products/{a}/content", new { title = "x", channel = "fb", body = "y", campaignId = campId })).StatusCode);
    }

    [Fact]
    public async Task Campaign_validation_and_status_rules()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/campaigns", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/campaigns", new { name = "x", startDate = "2026-12-10", endDate = "2026-12-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/products/{pid}/campaigns", new { name = "x", destinationUrl = "javascript:alert(1)" })).StatusCode);

        var ok = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/campaigns", new { name = "Winter", objective = "installs", budgetUsd = 100, destinationUrl = "https://example.com/x" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var id = (await ok.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/campaigns/{id}/status/Active", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/campaigns/{id}/status/Completed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/campaigns/{id}/status/Active", null)).StatusCode);   // completed is final
    }
}
