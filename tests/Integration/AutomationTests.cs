using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Automation;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class AutomationTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Auto " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private async Task DrainAsync()
    {
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 200 && await runner.RunOnceAsync(default); i++) { }
    }

    private static async Task<Guid> NewLeadAsync(HttpClient c, Guid pid, string first = "Rana")
    {
        var contact = await c.PostAsJsonAsync("/api/v1/crm/contacts", new { firstName = first, lastName = Uniq(), methods = new[] { new { type = "Email", value = $"a{Uniq()}@example.com" } } });
        var cid = (await contact.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var lead = await c.PostAsJsonAsync("/api/v1/crm/leads", new { contactId = cid, productId = pid, source = "manual" });
        Assert.Equal(HttpStatusCode.Created, lead.StatusCode);
        return (await lead.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> WorkflowAsync(HttpClient admin, Guid pid, string trigger, object[] steps, string name = "WF", bool enabled = true, object[]? conditions = null)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/automation/workflows", new { name = name + " " + Uniq(), trigger, productId = pid, enabled, definition = new { conditions = conditions ?? [], steps } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> RunsAsync(HttpClient admin, Guid wf) => await admin.GetFromJsonAsync<JsonElement>($"/api/v1/automation/workflows/{wf}/runs", Json);
    private static async Task<JsonElement> RunAsync(HttpClient admin, JsonElement run) => await admin.GetFromJsonAsync<JsonElement>($"/api/v1/automation/runs/{run.GetProperty("id").GetGuid()}", Json);

    [Fact]
    public async Task A_new_lead_starts_a_run_that_creates_an_assigned_task_and_notification_and_logs_every_step()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "task", title = "Call {{contactName}} today", dueInMinutes = 60, assignee = "owner" }, new { kind = "notify", message = "New lead: {{contactName}}" }]);

        await NewLeadAsync(admin, pid, "Zainab");
        await DrainAsync();

        var runs = await RunsAsync(admin, wf);
        var run = Assert.Single(runs.EnumerateArray());
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        var detail = await RunAsync(admin, run);
        var steps = detail.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal("Succeeded", s.GetProperty("status").GetString()));

        var tasks = await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks", Json);
        var task = Assert.Single(tasks.EnumerateArray(), t => t.GetProperty("title").GetString()!.StartsWith("Call Zainab"));
        Assert.NotNull(task.GetProperty("dueAt").GetString());
        var notes = await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications?unread=true", Json);
        Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("title").GetString()!.StartsWith("New lead: Zainab"));
    }

    [Fact]
    public async Task A_condition_on_the_trigger_decides_whether_a_run_does_anything()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "task", title = "Web lead", assignee = "owner" }], conditions: [new { field = "source", op = "eq", value = "website" }]);
        await NewLeadAsync(admin, pid);       // source = manual -> not a match
        await DrainAsync();
        var run = Assert.Single((await RunsAsync(admin, wf)).EnumerateArray());
        Assert.Equal("Skipped", run.GetProperty("status").GetString());
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks", Json)).EnumerateArray(), t => t.GetProperty("title").GetString() == "Web lead");
    }

    [Fact]
    public async Task No_reply_timeout_acts_only_if_the_lead_is_still_untouched_when_the_wait_ends()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var clock = host.Services.GetRequiredService<FakeClock>();
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "wait", minutes = 1440 }, new { kind = "if", field = "status", op = "eq", value = "New" }, new { kind = "notify", message = "No reply from {{contactName}}" }]);

        var ignored = await NewLeadAsync(admin, pid, "Ignored");
        var handled = await NewLeadAsync(admin, pid, "Handled");
        await DrainAsync();
        Assert.All((await RunsAsync(admin, wf)).EnumerateArray(), r => Assert.Equal("Waiting", r.GetProperty("status").GetString()));

        // Staff contact one of them before the day is up.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/crm/leads/{handled}/status", new { status = "Contacted" })).StatusCode);

        clock.Advance(TimeSpan.FromHours(25));
        await DrainAsync();

        var all = (await RunsAsync(admin, wf)).EnumerateArray().ToList();
        Assert.Equal(2, all.Count(r => r.GetProperty("status").GetString() == "Completed"));
        var notes = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Json)).EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        Assert.Contains(notes, n => n!.StartsWith("No reply from Ignored"));
        Assert.DoesNotContain(notes, n => n!.StartsWith("No reply from Handled"));
    }

    [Fact]
    public async Task A_paused_workflow_starts_nothing_and_cancels_runs_that_were_waiting()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var clock = host.Services.GetRequiredService<FakeClock>();
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "wait", minutes = 10 }, new { kind = "task", title = "Late task", assignee = "owner" }]);

        await NewLeadAsync(admin, pid);
        await DrainAsync();
        Assert.Equal("Waiting", Assert.Single((await RunsAsync(admin, wf)).EnumerateArray()).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/automation/workflows/{wf}/disable", null)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(11));
        await NewLeadAsync(admin, pid);       // arrives while paused
        await DrainAsync();

        var runs = (await RunsAsync(admin, wf)).EnumerateArray().ToList();
        Assert.Single(runs);                  // the new lead started nothing
        Assert.Equal("Cancelled", runs[0].GetProperty("status").GetString());
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks", Json)).EnumerateArray(), t => t.GetProperty("title").GetString() == "Late task");
    }

    [Fact]
    public async Task The_global_automation_pause_holds_work_until_lifted()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "task", title = "Held", assignee = "owner" }]);

        await admin.PutAsJsonAsync("/api/v1/governance/pause/Automation", new { isPaused = true, reason = "test" });
        try
        {
            await NewLeadAsync(admin, pid);
            await DrainAsync();
            Assert.Empty((await RunsAsync(admin, wf)).EnumerateArray());   // nothing ran
        }
        finally { await admin.PutAsJsonAsync("/api/v1/governance/pause/Automation", new { isPaused = false }); }

        host.Services.GetRequiredService<FakeClock>().Advance(TimeSpan.FromMinutes(2));   // the runner re-checks paused jobs every minute
        await DrainAsync();
        Assert.Equal("Completed", Assert.Single((await RunsAsync(admin, wf)).EnumerateArray()).GetProperty("status").GetString());   // the queued event was kept, not lost
    }

    [Fact]
    public async Task A_redelivered_event_does_not_start_a_second_run_or_repeat_effects()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "task", title = "Once only", assignee = "owner" }]);
        var leadId = await NewLeadAsync(admin, pid);
        await DrainAsync();

        // Re-send the very same event under different job keys, as a duplicate delivery would.
        var run = Assert.Single((await RunsAsync(admin, wf)).EnumerateArray());
        var eventId = await EventIdOf(run.GetProperty("id").GetGuid());
        using (var scope = host.Services.CreateScope())
        {
            var q = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var env = new EventEnvelope(eventId, "lead.created", pid, "lead", leadId, null);
            for (var i = 0; i < 3; i++) await q.EnqueueAsync(new JobRequest("automation.event", JsonSerializer.Serialize(env, Json), IdempotencyKey: $"dup-{i}-{eventId}"));
        }
        await DrainAsync();

        Assert.Single((await RunsAsync(admin, wf)).EnumerateArray());
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks", Json)).EnumerateArray(), t => t.GetProperty("title").GetString() == "Once only");
    }

    private async Task<Guid> EventIdOf(Guid runId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        return (await db.Runs.FindAsync(runId))!.EventId;
    }

    [Fact]
    public async Task A_webhook_step_that_fails_briefly_is_retried_without_repeating_earlier_steps()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var clock = host.Services.GetRequiredService<FakeClock>();
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "task", title = "Before n8n", assignee = "owner" }, new { kind = "webhook", name = "n8n-test" }], name: "flaky");

        await NewLeadAsync(admin, pid);
        await DrainAsync();
        var run = Assert.Single((await RunsAsync(admin, wf)).EnumerateArray());
        Assert.Equal("Running", run.GetProperty("status").GetString());     // failed once, waiting for the retry

        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();

        run = Assert.Single((await RunsAsync(admin, wf)).EnumerateArray());
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks", Json)).EnumerateArray(), t => t.GetProperty("title").GetString() == "Before n8n");
        var runId = run.GetProperty("id").GetGuid();
        Assert.Single(FakeWebhookClient.Calls, c => c.RunId == runId);
        var log = (await RunAsync(admin, run)).GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("status").GetString()).ToList();
        Assert.Contains("Failed", log);                                     // the failure is visible in the run log
        Assert.Equal("Succeeded", log.Last());
    }

    [Fact]
    public async Task Inbound_signed_webhooks_start_workflows_and_tampering_staleness_and_replay_are_refused()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var wf = await WorkflowAsync(admin, pid, "external.sync-done", [new { kind = "task", title = "From n8n", assignee = "none" }]);
        var anon = host.NewClient();
        const string secret = "n8n-test-shared-secret";
        var body = JsonSerializer.Serialize(new { eventType = "sync-done", subjectType = "lead", subjectId = Guid.NewGuid(), productId = pid });
        var now = host.Services.GetRequiredService<FakeClock>().UtcNow;

        HttpRequestMessage Req(string payload, long ts, string sig) => new(HttpMethod.Post, "/api/v1/webhooks/automation/n8n-test")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            Headers = { { "X-TE-Timestamp", ts.ToString() }, { "X-TE-Signature", sig } },
        };

        var ts = now.ToUnixTimeSeconds();
        var good = WebhookSigner.Sign(secret, ts, body);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(body + " ", ts, good))).StatusCode);                              // body altered
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(body, ts, WebhookSigner.Sign("wrong", ts, body)))).StatusCode);   // wrong secret
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(body, ts - 3600, WebhookSigner.Sign(secret, ts - 3600, body)))).StatusCode);   // stale
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/automation/nobody") { Content = new StringContent(body) })).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await anon.SendAsync(Req(body, ts, good))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anon.SendAsync(Req(body, ts, good))).StatusCode);                                        // identical request replayed
        await DrainAsync();

        Assert.Equal("Completed", Assert.Single((await RunsAsync(admin, wf)).EnumerateArray()).GetProperty("status").GetString());
        Assert.Contains((await admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks?scope=all", Json)).EnumerateArray(), t => t.GetProperty("title").GetString() == "From n8n");
    }

    [Fact]
    public async Task Overdue_tasks_raise_one_alert_and_show_in_the_work_summary()
    {
        var admin = await host.LoginAdminAsync();
        var clock = host.Services.GetRequiredService<FakeClock>();
        var title = "Chase " + Uniq();
        var created = await admin.PostAsJsonAsync("/api/v1/tasks", new { title, dueAt = clock.UtcNow.AddMinutes(30) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/v1/me/work-summary", Json);
        clock.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 2; i++)    // sweep twice: still one alert
        {
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("automation.sweep", "{}", IdempotencyKey: "sweep-" + Guid.NewGuid()));
            await DrainAsync();
        }

        var after = await admin.GetFromJsonAsync<JsonElement>("/api/v1/me/work-summary", Json);
        Assert.Equal(before.GetProperty("overdueTasks").GetInt32() + 1, after.GetProperty("overdueTasks").GetInt32());
        var alerts = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Json)).EnumerateArray().Where(n => n.GetProperty("title").GetString() == "Overdue: " + title).ToList();
        Assert.Single(alerts);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/tasks/{id}/complete", null)).StatusCode);
        Assert.Equal(after.GetProperty("overdueTasks").GetInt32() - 1, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/me/work-summary", Json)).GetProperty("overdueTasks").GetInt32());
    }

    [Fact]
    public async Task Editing_a_workflow_creates_a_new_version_and_stale_edits_are_refused()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var wf = await WorkflowAsync(admin, pid, "lead.created", [new { kind = "notify", message = "v1" }], enabled: false);
        var def = new { conditions = Array.Empty<object>(), steps = new object[] { new { kind = "notify", message = "v2" } } };

        var ok = await admin.PutAsJsonAsync($"/api/v1/automation/workflows/{wf}", new { definition = def, note = "second", baseVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/automation/workflows/{wf}", new { definition = def, baseVersion = 1 })).StatusCode);

        var got = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/automation/workflows/{wf}", Json);
        Assert.Equal(2, got.GetProperty("workflow").GetProperty("currentVersion").GetInt32());
        Assert.Equal(2, got.GetProperty("versions").GetArrayLength());
        Assert.Equal("v2", got.GetProperty("definition").GetProperty("steps")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Definitions_are_validated_and_access_is_limited_to_automation_managers()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        object Body(string trigger, object[] steps) => new { name = "Bad", trigger, productId = pid, definition = new { steps } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/automation/workflows", Body("lead.exploded", [new { kind = "notify", message = "x" }]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/automation/workflows", Body("lead.created", [new { kind = "webhook", name = "unknown" }]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/automation/workflows", Body("lead.created", []))).StatusCode);

        var email = $"viewer-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "V", password = "Some-Strong-Pass-123", roles = new[] { "Read-only Viewer" }, allProducts = true })).EnsureSuccessStatusCode();
        var viewer = await host.LoginAsync(email, "Some-Strong-Pass-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/automation/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync("/api/v1/automation/workflows", Body("lead.created", [new { kind = "notify", message = "x" }]))).StatusCode);

        // A viewer cannot complete someone else's task.
        var t = await admin.PostAsJsonAsync("/api/v1/tasks", new { title = "Admin only " + Uniq() });
        var tid = (await t.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync($"/api/v1/tasks/{tid}/complete", null)).StatusCode);
    }
}
