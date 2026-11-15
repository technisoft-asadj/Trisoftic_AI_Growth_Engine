using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class JobsTests(TestHost host)
{
    private (IJobQueue Queue, JobsDbContext Db, JobRunner Runner, FakeClock Clock, IServiceScope Scope) Open()
    {
        var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        return (sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<JobsDbContext>(), host.Services.GetRequiredService<JobRunner>(), host.Services.GetRequiredService<FakeClock>(), scope);
    }

    private static string Payload(string key, string? fail = null) => JsonSerializer.Serialize(new { key, fail });

    private async Task<Job> Reload(Guid id)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
        return await db.Jobs.FindAsync(id) ?? throw new InvalidOperationException();
    }

    [Fact]
    public async Task Successful_job_runs_once()
    {
        var (q, _, runner, _, scope) = Open(); using var _s = scope;
        var key = Guid.NewGuid().ToString();
        var id = await q.EnqueueAsync(new JobRequest("test.echo", Payload(key)));
        while (await runner.RunOnceAsync(default)) { }
        Assert.Equal(JobStatus.Succeeded, (await Reload(id)).Status);
        Assert.Equal(1, TestJobHandler.Executions[key]);
    }

    [Fact]
    public async Task Same_idempotency_key_yields_one_job_and_one_effect()
    {
        var (q, _, runner, _, scope) = Open(); using var _s = scope;
        var key = Guid.NewGuid().ToString();
        var ids = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            using var s = host.Services.CreateScope();   // one DbContext per concurrent caller
            return await s.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("test.echo", Payload(key), IdempotencyKey: "idem-" + key));
        })));
        Assert.Single(ids.Distinct());
        while (await runner.RunOnceAsync(default)) { }
        Assert.Equal(1, TestJobHandler.Executions[key]);
    }

    [Fact]
    public async Task Transient_failure_retries_with_backoff_then_dead_letters()
    {
        var (q, _, runner, clock, scope) = Open(); using var _s = scope;
        var key = Guid.NewGuid().ToString();
        var id = await q.EnqueueAsync(new JobRequest("test.echo", Payload(key, "transient"), MaxAttempts: 3));

        Assert.True(await runner.RunOnceAsync(default));
        var j = await Reload(id);
        Assert.Equal(JobStatus.Queued, j.Status);
        Assert.Equal(1, j.Attempts);
        Assert.True(j.RunAt > clock.UtcNow);                     // backed off
        Assert.False(await runner.RunOnceAsync(default));        // not due yet

        for (var i = 0; i < 2; i++) { clock.Advance(TimeSpan.FromHours(2)); await runner.RunOnceAsync(default); }
        j = await Reload(id);
        Assert.Equal(JobStatus.DeadLettered, j.Status);
        Assert.Equal(3, j.Attempts);
        Assert.Equal(3, TestJobHandler.Executions[key]);
    }

    [Fact]
    public async Task Permanent_failure_dead_letters_immediately_and_can_be_replayed_by_admin()
    {
        var (q, _, runner, _, scope) = Open(); using var _s = scope;
        var key = Guid.NewGuid().ToString();
        var id = await q.EnqueueAsync(new JobRequest("test.echo", Payload(key, "permanent")));
        await runner.RunOnceAsync(default);
        Assert.Equal(JobStatus.DeadLettered, (await Reload(id)).Status);

        var admin = await host.LoginAdminAsync();
        var r = await admin.PostAsync($"/api/v1/jobs/{id}/retry", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(JobStatus.Queued, (await Reload(id)).Status);

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/governance/audit?subjectType=Job", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "job.replayed");
    }

    [Fact]
    public async Task Paused_scope_blocks_execution_without_burning_attempts()
    {
        var (q, _, runner, clock, scope) = Open(); using var _s = scope;
        var key = Guid.NewGuid().ToString();
        var admin = await host.LoginAdminAsync();
        await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = true });
        TestJobHandler.Scope = PauseScope.Publishing;
        try
        {
            var id = await q.EnqueueAsync(new JobRequest("test.echo", Payload(key)));
            await runner.RunOnceAsync(default);
            var j = await Reload(id);
            Assert.Equal(JobStatus.Queued, j.Status);
            Assert.Equal(0, j.Attempts);
            Assert.False(TestJobHandler.Executions.ContainsKey(key));

            await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = false });
            clock.Advance(TimeSpan.FromMinutes(5));
            while (await runner.RunOnceAsync(default)) { }
            Assert.Equal(JobStatus.Succeeded, (await Reload(id)).Status);
        }
        finally
        {
            TestJobHandler.Scope = null;
            await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = false });
        }
    }

    [Fact]
    public async Task Parallel_workers_never_run_a_job_twice()
    {
        var (q, _, runner, _, scope) = Open(); using var _s = scope;
        var keys = Enumerable.Range(0, 24).Select(_ => Guid.NewGuid().ToString()).ToList();
        foreach (var k in keys) await q.EnqueueAsync(new JobRequest("test.echo", Payload(k)));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () => { while (await runner.RunOnceAsync(default)) { } })));

        Assert.All(keys, k => Assert.Equal(1, TestJobHandler.Executions[k]));
    }

    [Fact]
    public async Task Jobs_endpoints_require_permission()
    {
        var admin = await host.LoginAdminAsync();
        await admin.PostAsJsonAsync("/api/v1/users", new
        {
            email = "viewer@test.local", displayName = "V", password = "Viewer-Password-123",
            roles = new[] { "Read-only Viewer" }, allProducts = true,
        });
        var viewer = await host.LoginAsync("viewer@test.local", "Viewer-Password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/jobs")).StatusCode);
    }
}
