using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Jobs;
using TriSoftic.GrowthEngine.Modules.Publishing;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class PublishingTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Pub " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"pub-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private static async Task ConnectSandbox(HttpClient admin, Guid pid, string channel = "facebook")
        => Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel, name = "Demo page" })).StatusCode);

    /// <summary>An approved item scheduled 30 minutes from the fake clock's now.</summary>
    private async Task<(Guid Id, string Text)> ScheduledAsync(HttpClient admin, HttpClient approver, Guid pid, string? text = null, string format = "Post", string channel = "facebook")
    {
        text ??= "Launch day! " + Uniq();
        var r = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "Post", channel, format, body = text });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/content/{id}/submit", null)).StatusCode);
        var ap = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={id}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/api/v1/approvals/{ap}/decision", new { decision = "Approved" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddMinutes(30) })).StatusCode);
        return (id, text);
    }

    private async Task DispatchAsync()
    {
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("publishing.dispatch", "{}", IdempotencyKey: "d-" + Guid.NewGuid()));
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 200 && await runner.RunOnceAsync(default); i++) { }
    }

    /// <summary>Runs the clock forward in steps, dispatching and draining each time, until the item reaches one of the statuses (or the steps run out).</summary>
    private async Task<string> WaitForAsync(HttpClient admin, Guid id, params string[] statuses)
    {
        string status = "";
        for (var i = 0; i < 8; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(i == 0 ? 31 : 45));
            await DispatchAsync();
            status = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{id}", Json)).GetProperty("item").GetProperty("status").GetString()!;
            if (statuses.Contains(status)) break;
        }
        return status;
    }

    private static async Task<JsonElement> ItemAsync(HttpClient c, Guid id) => (await c.GetFromJsonAsync<JsonElement>($"/api/v1/content/{id}", Json)).GetProperty("item");

    [Fact]
    public async Task A_due_approved_post_is_published_once_and_marked_published_only_after_the_provider_confirms()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var (id, text) = await ScheduledAsync(admin, approver, pid);

        await DispatchAsync();                                                      // not due yet
        Assert.Equal("Scheduled", (await ItemAsync(admin, id)).GetProperty("status").GetString());
        Assert.Equal(0, ScriptedPublisher.LiveCount(text));

        Assert.Equal("Published", await WaitForAsync(admin, id, "Published"));
        await DispatchAsync(); await DispatchAsync();                               // more dispatches change nothing
        Assert.Equal(1, ScriptedPublisher.LiveCount(text));

        var posts = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/posts", Json);
        var post = Assert.Single(posts.EnumerateArray());
        Assert.StartsWith("post-", post.GetProperty("providerPostId").GetString());
        Assert.StartsWith("https://social.test/", post.GetProperty("url").GetString());
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={pid}&take=100", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "publishing.published");
    }

    [Fact]
    public async Task Post_figures_are_stored_per_post_and_a_figure_the_network_withheld_stays_empty_not_zero()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var (id, _) = await ScheduledAsync(admin, approver, pid);
        Assert.Equal("Published", await WaitForAsync(admin, id, "Published"));
        var runner = host.Services.GetRequiredService<JobRunner>();
        async Task Pull()
        {
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("publishing.pull-post-metrics", "{}", IdempotencyKey: "pm-" + Guid.NewGuid()));
            for (var k = 0; k < 100 && await runner.RunOnceAsync(default); k++) { }
        }
        async Task<JsonElement> Row() => Assert.Single((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/post-metrics", Json)).EnumerateArray());

        ScriptedPublisher.PostFigures = null;                                       // this adapter cannot report on posts
        await Pull();
        Assert.Equal(JsonValueKind.Null, (await Row()).GetProperty("reactions").ValueKind);   // nothing measured is not zero

        ScriptedPublisher.PostFigures = new(Reactions: 12, Comments: 3, Shares: 0, Views: null, Reach: null, Clicks: null, Note: "Facebook did not supply views, reach, clicks (the connection may lack the read_insights permission, or the post is too new).");
        try { await Pull(); }
        finally { ScriptedPublisher.PostFigures = null; }
        var row = await Row();
        Assert.Equal(12, row.GetProperty("reactions").GetInt32()); Assert.Equal(0, row.GetProperty("shares").GetInt32());   // a real zero is kept
        Assert.Equal(JsonValueKind.Null, row.GetProperty("views").ValueKind);                                           // withheld: still empty
        Assert.Contains("read_insights", row.GetProperty("note").GetString());

        // The same figures reach analytics only where every figure needed is known: engagements yes, impressions no.
        var summary = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/analytics/summary?productId={pid}&channel=facebook", Json);
        JsonElement T(string key) => summary.GetProperty("tiles").EnumerateArray().First(t => t.GetProperty("key").GetString() == key);
        Assert.Equal(15m, T("engagements").GetProperty("value").GetDecimal());
        Assert.Equal("Unavailable", T("impressions").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Page_figures_keep_a_refused_figure_empty_with_its_reason_and_never_erase_one_that_was_reported()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var runner = host.Services.GetRequiredService<JobRunner>();
        async Task Pull()
        {
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("publishing.pull-page-figures", "{}", IdempotencyKey: "pf-" + Guid.NewGuid()));
            for (var k = 0; k < 100 && await runner.RunOnceAsync(default); k++) { }
        }
        async Task<JsonElement> Fig(string name) => (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/page-health", Json)).GetProperty("figures").EnumerateArray().First(f => f.GetProperty("figure").GetString() == name);
        var start = Clock.UtcNow; var today = DateOnly.FromDateTime(start.UtcDateTime);
        PageFigureResult R(string f, string? metric, string? reason, params (int DaysAgo, long V)[] v) => new(f, metric, reason, v.Select(x => new PageFigureValue(today.AddDays(-x.DaysAgo), x.V)).ToList());

        try
        {
            ScriptedPublisher.PageFigures = null;                                      // this adapter cannot report on the Page
            await Pull();
            Assert.Equal(JsonValueKind.Null, (await Fig("followers")).GetProperty("value").ValueKind);

            ScriptedPublisher.PageFigures =
            [
                R("followers", "followers_count", null, (0, 120)),
                R("page_views", "page_media_view", null, (2, 40), (1, 55)),
                R("page_reach", null, "page_total_media_view_unique: (#100) The value must be a valid insights metric"),
                R("new_follows", "page_daily_follows_unique", null, (1, 0)),            // a real zero is kept
            ];
            await Pull();
            var followers = await Fig("followers");
            Assert.Equal(120, followers.GetProperty("value").GetInt64()); Assert.Equal("followers_count", followers.GetProperty("metric").GetString());
            Assert.Equal(JsonValueKind.Null, followers.GetProperty("change").ValueKind);   // one reading is not a change
            Assert.Equal(2, (await Fig("page_views")).GetProperty("series").GetArrayLength());
            var reach = await Fig("page_reach");
            Assert.Equal(JsonValueKind.Null, reach.GetProperty("value").ValueKind);        // refused: empty, not zero
            Assert.Contains("valid insights metric", reach.GetProperty("reason").GetString());
            Assert.Equal(0, (await Fig("new_follows")).GetProperty("value").GetInt64());

            // a later refusal does not erase what was reported
            ScriptedPublisher.PageFigures = [R("followers", null, "Facebook did not return followers_count for this Page")];
            await Pull();
            Assert.Equal(120, (await Fig("followers")).GetProperty("value").GetInt64());

            // two days later the count is higher: the change is measured from the first reading of the week
            Clock.Advance(TimeSpan.FromDays(2)); var later = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime);
            ScriptedPublisher.PageFigures = [new PageFigureResult("followers", "followers_count", null, [new PageFigureValue(later, 133)])];
            await Pull();
            followers = await Fig("followers");
            Assert.Equal(133, followers.GetProperty("value").GetInt64());
            Assert.Equal(13, followers.GetProperty("change").GetInt64());
            Assert.Equal(today.ToString("yyyy-MM-dd"), followers.GetProperty("changeSince").GetString());
        }
        finally { ScriptedPublisher.PageFigures = null; Clock.Set(start); }
    }

    [Fact]
    public async Task A_transient_provider_failure_is_retried_and_creates_exactly_one_post()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Retry me " + Uniq(); ScriptedPublisher.Queue(text, "retryable", "retryable");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);

        Assert.Equal("Published", await WaitForAsync(admin, id, "Published", "PublishFailed"));
        Assert.Equal(3, ScriptedPublisher.Calls[text]);
        Assert.Equal(1, ScriptedPublisher.LiveCount(text));
    }

    [Fact]
    public async Task When_the_provider_accepted_the_post_but_we_got_a_timeout_the_retry_finds_it_instead_of_posting_again()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Timeout after accept " + Uniq(); ScriptedPublisher.Queue(text, "unknown-accepted");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);

        Assert.Equal("Published", await WaitForAsync(admin, id, "Published", "PublishFailed"));
        Assert.Equal(1, ScriptedPublisher.Calls[text]);            // sent once; the retry only looked
        Assert.Equal(1, ScriptedPublisher.LiveCount(text));        // and no duplicate exists
    }

    [Fact]
    public async Task When_the_timeout_meant_nothing_went_out_the_retry_posts_it()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Timeout lost " + Uniq(); ScriptedPublisher.Queue(text, "unknown-lost");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);

        Assert.Equal("Published", await WaitForAsync(admin, id, "Published", "PublishFailed"));
        Assert.Equal(2, ScriptedPublisher.Calls[text]);
        Assert.Equal(1, ScriptedPublisher.LiveCount(text));
    }

    [Fact]
    public async Task A_refused_post_shows_as_failed_with_the_reason_and_can_be_retried()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Refused " + Uniq(); ScriptedPublisher.Queue(text, "permanent");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);

        Assert.Equal("PublishFailed", await WaitForAsync(admin, id, "Published", "PublishFailed"));
        var failures = await admin.GetFromJsonAsync<JsonElement>("/api/v1/publishing/failures", Json);
        var f = Assert.Single(failures.EnumerateArray(), x => x.GetProperty("itemId").GetGuid() == id);
        Assert.Contains("text rejected by policy", f.GetProperty("reason").GetString());
        Assert.Equal(0, ScriptedPublisher.LiveCount(text));

        // The provider now accepts it: retry from the failure list.
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync($"/api/v1/publishing/content/{id}/retry", new { version = 1 })).StatusCode);
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 50 && await runner.RunOnceAsync(default); i++) { }
        Assert.Equal("Published", (await ItemAsync(admin, id)).GetProperty("status").GetString());
        Assert.Equal(1, ScriptedPublisher.LiveCount(text));
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/v1/publishing/failures", Json)).EnumerateArray(), x => x.GetProperty("itemId").GetGuid() == id);
    }

    [Fact]
    public async Task Running_out_of_retries_ends_as_failed_not_stuck()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Always limited " + Uniq(); ScriptedPublisher.Queue(text, "retryable", "retryable", "retryable", "retryable", "retryable");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);

        Assert.Equal("PublishFailed", await WaitForAsync(admin, id, "Published", "PublishFailed"));
        var item = await ItemAsync(admin, id);
        Assert.Equal(0, ScriptedPublisher.LiveCount(text));
        var f = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/publishing/failures", Json)).EnumerateArray().Single(x => x.GetProperty("itemId").GetGuid() == id);
        Assert.Contains("Giving up", f.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_revoked_account_is_flagged_for_reconnecting_and_publishing_resumes_after_reconnect()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Revoked " + Uniq(); ScriptedPublisher.Queue(text, "auth");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);

        Assert.Equal("PublishFailed", await WaitForAsync(admin, id, "Published", "PublishFailed"));
        var accounts = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/accounts", Json);
        Assert.Equal("NeedsReconnect", Assert.Single(accounts.EnumerateArray()).GetProperty("status").GetString());

        await ConnectSandbox(admin, pid);                                    // reconnect
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync($"/api/v1/publishing/content/{id}/retry", new { version = 1 })).StatusCode);
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 50 && await runner.RunOnceAsync(default); i++) { }
        Assert.Equal("Published", (await ItemAsync(admin, id)).GetProperty("status").GetString());
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/accounts", Json)).EnumerateArray());   // the old connection was retired
    }

    [Fact]
    public async Task Content_that_cannot_be_published_is_refused_up_front_with_a_clear_reason()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var noAccount = await ProductAsync(admin);
        var (a, _) = await ScheduledAsync(admin, approver, noAccount);
        Assert.Equal("PublishFailed", await WaitForAsync(admin, a, "PublishFailed", "Published"));
        Assert.Contains("No account is connected", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/publishing/failures", Json)).EnumerateArray().Single(x => x.GetProperty("itemId").GetGuid() == a).GetProperty("reason").GetString());

        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var (b, _) = await ScheduledAsync(admin, approver, pid, format: "Blog");
        Assert.Equal("PublishFailed", await WaitForAsync(admin, b, "PublishFailed", "Published"));
        Assert.Contains("cannot be published", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/publishing/failures", Json)).EnumerateArray().Single(x => x.GetProperty("itemId").GetGuid() == b).GetProperty("reason").GetString());

        var (c, longText) = await ScheduledAsync(admin, approver, pid, text: new string('x', 401));
        Assert.Equal("PublishFailed", await WaitForAsync(admin, c, "PublishFailed", "Published"));
        Assert.Equal(0, ScriptedPublisher.LiveCount(longText));
    }

    [Fact]
    public async Task Editing_a_scheduled_post_takes_it_out_of_the_queue_so_the_old_text_never_goes_out()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var (id, text) = await ScheduledAsync(admin, approver, pid);

        var edit = await admin.PostAsJsonAsync($"/api/v1/content/{id}/versions", new { body = "A completely different text " + Uniq(), baseVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Clock.Advance(TimeSpan.FromHours(2)); await DispatchAsync();

        Assert.Equal("Draft", (await ItemAsync(admin, id)).GetProperty("status").GetString());
        Assert.Equal(0, ScriptedPublisher.LiveCount(text));
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/posts", Json)).EnumerateArray());
    }

    [Fact]
    public async Task The_publishing_pause_holds_posts_until_it_is_lifted()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var (id, text) = await ScheduledAsync(admin, approver, pid);

        await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = true, reason = "test" });
        try
        {
            Clock.Advance(TimeSpan.FromHours(1)); await DispatchAsync();
            Assert.Equal("Scheduled", (await ItemAsync(admin, id)).GetProperty("status").GetString());
            Assert.Equal(0, ScriptedPublisher.LiveCount(text));
        }
        finally { await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = false }); }

        Assert.Equal("Published", await WaitForAsync(admin, id, "Published"));
        Assert.Equal(1, ScriptedPublisher.LiveCount(text));
    }

    [Fact]
    public async Task A_failed_post_can_be_marked_published_by_hand_with_its_link_and_the_action_is_audited()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Posted by hand " + Uniq(); ScriptedPublisher.Queue(text, "permanent");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);
        Assert.Equal("PublishFailed", await WaitForAsync(admin, id, "PublishFailed", "Published"));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/publishing/content/{id}/mark-published", new { version = 1, url = "not a link" })).StatusCode);
        var ok = await admin.PostAsJsonAsync($"/api/v1/publishing/content/{id}/mark-published", new { version = 1, url = "https://facebook.com/page/posts/123", note = "Posted from the phone" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal("Published", (await ItemAsync(admin, id)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/publishing/content/{id}/mark-published", new { version = 1, url = "https://facebook.com/x" })).StatusCode);   // no longer failed

        var post = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/posts", Json)).EnumerateArray().Single();
        Assert.Equal("Manual", post.GetProperty("source").GetString());
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={pid}&take=100", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "publishing.marked-published-manually");
        Assert.Equal(0, ScriptedPublisher.LiveCount(text));   // the platform itself never posted it
    }

    [Fact]
    public async Task A_failed_post_can_be_abandoned_and_stays_approved()
    {
        var admin = await host.LoginAdminAsync(); var approver = await UserAsync(admin, "Approver");
        var pid = await ProductAsync(admin); await ConnectSandbox(admin, pid);
        var text = "Give up " + Uniq(); ScriptedPublisher.Queue(text, "permanent");
        var (id, _) = await ScheduledAsync(admin, approver, pid, text);
        Assert.Equal("PublishFailed", await WaitForAsync(admin, id, "PublishFailed", "Published"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/publishing/content/{id}/abandon", new { version = 1 })).StatusCode);
        Assert.Equal("Approved", (await ItemAsync(admin, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Connecting_facebook_goes_through_a_signed_state_and_a_page_choice_and_never_exposes_tokens()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var start = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/connect/facebook/start?productId={pid}&channel=facebook", Json);
        var url = new Uri(start.GetProperty("url").GetString()!);
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"]!;

        var anon = host.NewClient();                                          // the browser returns from Facebook without our session cookie
        var bad = await anon.GetAsync($"/api/v1/publishing/connect/facebook/callback?code=abc&state={Uri.EscapeDataString(state + "x")}");
        Assert.Contains("error=expired", bad.Headers.Location!.ToString());
        var denied = await anon.GetAsync("/api/v1/publishing/connect/facebook/callback?error=access_denied");
        Assert.Contains("error=denied", denied.Headers.Location!.ToString());

        var ok = await anon.GetAsync($"/api/v1/publishing/connect/facebook/callback?code=abc&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var loc = ok.Headers.Location!.OriginalString;
        var pending = Guid.Parse(System.Web.HttpUtility.ParseQueryString(loc[(loc.IndexOf('?') + 1)..])["pending"]!);

        var otherUser = await UserAsync(admin, "Marketing Manager");
        Assert.Equal(HttpStatusCode.NotFound, (await otherUser.GetAsync($"/api/v1/publishing/connect/pending/{pending}")).StatusCode);   // only the person who started it

        var rawPages = await admin.GetStringAsync($"/api/v1/publishing/connect/pending/{pending}");
        Assert.DoesNotContain("page-secret-token", rawPages);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/publishing/connect/pending/{pending}/select", new { pageId = "999" })).StatusCode);
        var sel = await admin.PostAsJsonAsync($"/api/v1/publishing/connect/pending/{pending}/select", new { pageId = "111" });
        Assert.Equal(HttpStatusCode.OK, sel.StatusCode);
        Assert.DoesNotContain("page-secret-token", await sel.Content.ReadAsStringAsync());

        var raw = await admin.GetStringAsync($"/api/v1/publishing/products/{pid}/accounts");
        Assert.Contains("Ludo Fight Official", raw);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cipher", raw, StringComparison.OrdinalIgnoreCase);

        // Disconnect destroys the stored permission and hides the account.
        var id = JsonDocument.Parse(raw).RootElement[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/publishing/accounts/{id}/disconnect", null)).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/accounts", Json)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/publishing/connect/pending/{pending}")).StatusCode);   // single use
    }

    [Fact]
    public async Task Publishing_actions_need_the_publishing_permission()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var viewer = await UserAsync(admin, "Read-only Viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/publishing/products/{pid}/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/publishing/failures")).StatusCode);
    }
}
