using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Automation;

public sealed record EventEnvelope(Guid EventId, string Type, Guid? ProductId, string SubjectType, Guid SubjectId, Dictionary<string, string?>? Data);

public sealed record WebhookCall(string Name, Guid RunId, int Step, Guid EventId, string WorkflowName, string SubjectType, Guid SubjectId, IReadOnlyDictionary<string, object?> Context);

/// <summary>Calls out to an external automation tool (n8n). It is used for input/output only: decisions stay in this engine.</summary>
public interface IWebhookClient
{
    bool IsConfigured(string name);
    /// <summary>Throws <see cref="TransientJobException"/> for network/5xx/429 and <see cref="PermanentJobException"/> for other failures.</summary>
    Task SendAsync(WebhookCall call, CancellationToken ct);
}

public sealed class WebhookClient(IHttpClientFactory http, IConfiguration cfg, IClock clock) : IWebhookClient
{
    public bool IsConfigured(string name) => !string.IsNullOrEmpty(cfg[$"Automation:Webhooks:{name}:Url"]) && !string.IsNullOrEmpty(cfg[$"Automation:Webhooks:{name}:Secret"]);

    public async Task SendAsync(WebhookCall call, CancellationToken ct)
    {
        var url = cfg[$"Automation:Webhooks:{call.Name}:Url"]; var secret = cfg[$"Automation:Webhooks:{call.Name}:Secret"];
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(secret)) throw new PermanentJobException($"Webhook '{call.Name}' is not configured");

        var body = JsonSerializer.Serialize(new
        {
            runId = call.RunId, step = call.Step, eventId = call.EventId, workflow = call.WorkflowName,
            subject = new { type = call.SubjectType, id = call.SubjectId }, context = call.Context,
        });
        var ts = clock.UtcNow.ToUnixTimeSeconds();
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-TE-Timestamp", ts.ToString());
        req.Headers.Add("X-TE-Signature", WebhookSigner.Sign(secret, ts, body));
        req.Headers.Add("X-TE-Idempotency-Key", $"{call.RunId}:{call.Step}");   // the receiver can ignore a repeat of the same step

        HttpResponseMessage res;
        try { res = await http.CreateClient("automation-webhooks").SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new TransientJobException($"Webhook '{call.Name}' could not be reached", inner: ex); }
        using (res)
        {
            if (res.IsSuccessStatusCode) return;
            var code = (int)res.StatusCode;
            if (code == 429 || code >= 500) throw new TransientJobException($"Webhook '{call.Name}' answered {code}", res.Headers.RetryAfter?.Delta);
            throw new PermanentJobException($"Webhook '{call.Name}' rejected the call with {code}");
        }
    }
}

public sealed class WorkflowEngine(AutomationDbContext db, IEnumerable<ISubjectStateProvider> providers, IJobQueue jobs, IClock clock, IWebhookClient hooks)
{
    public const string ResumeJob = "automation.resume";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<Dictionary<string, object?>> ContextAsync(string subjectType, Guid subjectId, Dictionary<string, string?>? data, CancellationToken ct)
    {
        var ctx = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in data ?? []) ctx[k] = v;
        var provider = providers.FirstOrDefault(p => p.SubjectType == subjectType);
        if (provider is not null && await provider.GetAsync(subjectId, ct) is { } state) foreach (var (k, v) in state) ctx[k] = v;   // current facts win over publish-time hints
        return ctx;
    }

    /// <summary>Starts (or continues, after a retry) one run per matching workflow. Safe to call twice with the same event.</summary>
    public async Task HandleEventAsync(EventEnvelope e, bool lastAttempt, CancellationToken ct)
    {
        var workflows = await db.Workflows.AsNoTracking().Where(w => w.Enabled && w.Trigger == e.Type && (w.ProductId == null || w.ProductId == e.ProductId)).ToListAsync(ct);
        Exception? failure = null;
        foreach (var wf in workflows)
        {
            try
            {
                var run = await db.Runs.FirstOrDefaultAsync(r => r.WorkflowId == wf.Id && r.EventId == e.EventId, ct);
                var def = WorkflowDefinition.FromJson((await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == wf.Id && v.Number == wf.CurrentVersion, ct)).DefinitionJson);
                if (run is null)
                {
                    var ctx = await ContextAsync(e.SubjectType, e.SubjectId, e.Data, ct);
                    var now = clock.UtcNow;
                    run = new WorkflowRun
                    {
                        WorkflowId = wf.Id, VersionNumber = wf.CurrentVersion, EventId = e.EventId, EventType = e.Type, ProductId = e.ProductId, SubjectType = e.SubjectType, SubjectId = e.SubjectId,
                        EventDataJson = JsonSerializer.Serialize(e.Data ?? [], Json), Status = RunStatus.Running, CreatedAt = now, UpdatedAt = now,
                    };
                    if (!Definitions.AllMatch(ctx, def.Conditions))
                    {
                        run.Status = RunStatus.Skipped; run.CompletedAt = now; run.Error = "Conditions not met; nothing was done";
                    }
                    db.Runs.Add(run);
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { db.ChangeTracker.Clear(); continue; }   // a parallel delivery got there first
                }
                if (run.Status == RunStatus.Waiting && run.ResumeAt is { } at) await EnqueueResume(run.Id, run.NextStep, at, run.ProductId, ct);   // heal a lost resume job
                if (run.Status == RunStatus.Running) await ExecuteAsync(run, def, wf.Name, lastAttempt, ct);
            }
            catch (TransientJobException ex) { failure ??= ex; db.ChangeTracker.Clear(); }
        }
        if (failure is not null) throw failure;
    }

    public async Task ResumeAsync(Guid runId, bool lastAttempt, CancellationToken ct)
    {
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status is not (RunStatus.Waiting or RunStatus.Running)) return;   // already finished, cancelled, or resumed
        var wf = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == run.WorkflowId, ct);
        if (!wf.Enabled)
        {
            run.Status = RunStatus.Cancelled; run.Error = "The workflow was paused while this run was waiting"; run.CompletedAt = clock.UtcNow; run.UpdatedAt = run.CompletedAt.Value;
            Log(run, run.NextStep, "resume", LogStatus.Skipped, "Workflow is paused; the remaining steps were not run");
            await db.SaveChangesAsync(ct);
            return;
        }
        var def = WorkflowDefinition.FromJson((await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == wf.Id && v.Number == run.VersionNumber, ct)).DefinitionJson);
        run.Status = RunStatus.Running; run.ResumeAt = null;
        await ExecuteAsync(run, def, wf.Name, lastAttempt, ct);
    }

    private Task EnqueueResume(Guid runId, int step, DateTimeOffset at, Guid? productId, CancellationToken ct)
        => jobs.EnqueueAsync(new JobRequest(ResumeJob, JsonSerializer.Serialize(new { runId }, Json), IdempotencyKey: $"resume:{runId}:{step}", RunAt: at, ProductId: productId), ct);

    private void Log(WorkflowRun run, int step, string kind, LogStatus status, string? detail)
        => db.Logs.Add(new ActionLog { RunId = run.Id, StepIndex = step, Kind = kind, Status = status, Detail = detail is { Length: > 2000 } ? detail[..2000] : detail, At = clock.UtcNow });

    private async Task ExecuteAsync(WorkflowRun run, WorkflowDefinition def, string workflowName, bool lastAttempt, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, string?>>(run.EventDataJson, Json);
        var ctx = await ContextAsync(run.SubjectType, run.SubjectId, data, ct);
        if (!ctx.ContainsKey("ownerUserId")) ctx["ownerUserId"] = null;

        for (var i = run.NextStep; i < def.Steps.Count; i++)
        {
            var s = def.Steps[i];
            try
            {
                switch (s.Kind)
                {
                    case "wait":
                        run.NextStep = i + 1; run.Status = RunStatus.Waiting; run.ResumeAt = clock.UtcNow.AddMinutes(s.Minutes!.Value); run.UpdatedAt = clock.UtcNow;
                        Log(run, i, "wait", LogStatus.Waiting, $"Waiting {s.Minutes} minutes");
                        await db.SaveChangesAsync(ct);            // persist first, then schedule: a lost job is healed by HandleEventAsync on redelivery
                        await EnqueueResume(run.Id, run.NextStep, run.ResumeAt.Value, run.ProductId, ct);
                        return;

                    case "if":
                        if (!Definitions.Matches(ctx, s.Field!, s.Op!, s.Value))
                        {
                            Log(run, i, "if", LogStatus.Skipped, $"{s.Field} {s.Op} {s.Value} is not true now; stopped here");
                            Finish(run, RunStatus.Completed, "Stopped at a condition that was no longer true");
                            await db.SaveChangesAsync(ct);
                            return;
                        }
                        Log(run, i, "if", LogStatus.Succeeded, $"{s.Field} {s.Op} {s.Value} holds");
                        break;

                    case "task": await DoTask(run, i, s, ctx, ct); break;
                    case "notify": await DoNotify(run, i, s, ctx, ct); break;

                    case "webhook":
                        await hooks.SendAsync(new WebhookCall(s.Name!, run.Id, i, run.EventId, workflowName, run.SubjectType, run.SubjectId, ctx), ct);
                        Log(run, i, "webhook", LogStatus.Succeeded, $"Sent to '{s.Name}'");
                        break;

                    default:
                        throw new PermanentJobException($"Step kind '{s.Kind}' is not available");
                }
                run.NextStep = i + 1; run.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (TransientJobException ex)
            {
                db.ChangeTracker.Clear();
                var fresh = await db.Runs.FirstAsync(r => r.Id == run.Id, ct);
                Log(fresh, i, s.Kind, LogStatus.Failed, ex.Message + (lastAttempt ? " (giving up)" : " (will retry)"));
                if (lastAttempt) Finish(fresh, RunStatus.Failed, ex.Message);
                await db.SaveChangesAsync(ct);
                throw;
            }
            catch (PermanentJobException ex)
            {
                db.ChangeTracker.Clear();
                var fresh = await db.Runs.FirstAsync(r => r.Id == run.Id, ct);
                Log(fresh, i, s.Kind, LogStatus.Failed, ex.Message);
                Finish(fresh, RunStatus.Failed, ex.Message);
                await db.SaveChangesAsync(ct);
                return;   // not retried: a human has to fix the workflow
            }
        }
        Finish(run, RunStatus.Completed, null);
        await db.SaveChangesAsync(ct);
    }

    private void Finish(WorkflowRun run, RunStatus status, string? note)
    {
        run.Status = status; run.CompletedAt = clock.UtcNow; run.UpdatedAt = run.CompletedAt.Value; run.ResumeAt = null;
        if (note is not null) run.Error = status == RunStatus.Failed ? note : run.Error;
    }

    private static Guid? Recipient(string? who, IReadOnlyDictionary<string, object?> ctx)
    {
        if (who is null or "owner") return ctx.TryGetValue("ownerUserId", out var o) && Guid.TryParse(Convert.ToString(o), out var g) ? g : null;
        return Guid.TryParse(who, out var id) ? id : null;
    }

    private static string? LinkFor(string subjectType, Guid id) => subjectType switch
    {
        "lead" => $"/crm/leads/{id}", "opportunity" => $"/crm/deals/{id}", "booking" => "/crm/appointments", _ => null,
    };

    private async Task DoTask(WorkflowRun run, int i, Step s, Dictionary<string, object?> ctx, CancellationToken ct)
    {
        if (await db.Tasks.AnyAsync(t => t.RunId == run.Id && t.StepIndex == i, ct)) { Log(run, i, "task", LogStatus.Skipped, "Task already created by an earlier attempt"); return; }
        var assignee = s.Assignee == "none" ? null : Recipient(s.Assignee, ctx);
        if (assignee is null && s.Assignee != "none") { Log(run, i, "task", LogStatus.Skipped, "No owner to assign to; task was not created"); return; }
        var now = clock.UtcNow;
        var task = new WorkTask
        {
            ProductId = run.ProductId, Title = Definitions.Render(s.Title!, ctx, 300), Notes = s.Notes is null ? null : Definitions.Render(s.Notes, ctx, 4000), AssigneeUserId = assignee,
            DueAt = s.DueInMinutes is { } m ? now.AddMinutes(m) : null, SubjectType = run.SubjectType, SubjectId = run.SubjectId, RunId = run.Id, StepIndex = i, CreatedAt = now,
        };
        db.Tasks.Add(task);
        if (assignee is { } uid) db.Notifications.Add(new Notification { UserId = uid, Title = "New task: " + task.Title, Link = LinkFor(run.SubjectType, run.SubjectId), RunId = run.Id, StepIndex = i, CreatedAt = now });
        Log(run, i, "task", LogStatus.Succeeded, $"Created task '{task.Title}'");
    }

    private async Task DoNotify(WorkflowRun run, int i, Step s, Dictionary<string, object?> ctx, CancellationToken ct)
    {
        if (await db.Notifications.AnyAsync(n => n.RunId == run.Id && n.StepIndex == i, ct)) { Log(run, i, "notify", LogStatus.Skipped, "Already notified by an earlier attempt"); return; }
        var to = Recipient(s.To, ctx);
        if (to is null) { Log(run, i, "notify", LogStatus.Skipped, "No recipient (the record has no owner)"); return; }
        db.Notifications.Add(new Notification { UserId = to.Value, Title = Definitions.Render(s.Message!, ctx, 300), Link = LinkFor(run.SubjectType, run.SubjectId), RunId = run.Id, StepIndex = i, CreatedAt = clock.UtcNow });
        Log(run, i, "notify", LogStatus.Succeeded, "Notified");
    }
}

/// <summary>Publishes events as jobs, so the work happens in the background and survives restarts.</summary>
public sealed class JobEventPublisher(IJobQueue jobs, ILogger<JobEventPublisher> log) : IEventPublisher
{
    public const string EventJob = "automation.event";

    public async Task PublishAsync(PlatformEvent e, CancellationToken ct = default)
    {
        try
        {
            var id = Guid.NewGuid();
            var env = new EventEnvelope(id, e.Type, e.ProductId, e.SubjectType, e.SubjectId, e.Data is null ? null : new Dictionary<string, string?>(e.Data));
            await jobs.EnqueueAsync(new JobRequest(EventJob, JsonSerializer.Serialize(env, new JsonSerializerOptions(JsonSerializerDefaults.Web)), IdempotencyKey: id.ToString(), ProductId: e.ProductId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not publish event {Type} for {Subject} {Id}", e.Type, e.SubjectType, e.SubjectId);   // the business change already committed; do not undo it
        }
    }
}

public sealed class AutomationEventHandler(WorkflowEngine engine) : IJobHandler
{
    public string Type => JobEventPublisher.EventJob;
    public PauseScope? PauseScope => BuildingBlocks.Abstractions.PauseScope.Automation;

    public Task HandleAsync(JobContext c, CancellationToken ct)
    {
        var env = JsonSerializer.Deserialize<EventEnvelope>(c.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new PermanentJobException("Unreadable event payload");
        return engine.HandleEventAsync(env, c.Attempt >= c.MaxAttempts, ct);
    }
}

public sealed class AutomationResumeHandler(WorkflowEngine engine) : IJobHandler
{
    public string Type => WorkflowEngine.ResumeJob;
    public PauseScope? PauseScope => BuildingBlocks.Abstractions.PauseScope.Automation;

    public Task HandleAsync(JobContext c, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(c.PayloadJson);
        return engine.ResumeAsync(doc.RootElement.GetProperty("runId").GetGuid(), c.Attempt >= c.MaxAttempts, ct);
    }
}

/// <summary>Raises one in-app alert per task that has passed its due time without being completed.</summary>
public sealed class OverdueSweepHandler(AutomationDbContext db, IClock clock) : IJobHandler
{
    public const string SweepJob = "automation.sweep";
    public string Type => SweepJob;
    public PauseScope? PauseScope => null;   // alerts to staff are not outbound automation

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var overdue = await db.Tasks.Where(t => t.CompletedAt == null && t.DueAt < now && t.OverdueNotifiedAt == null && t.AssigneeUserId != null).Take(500).ToListAsync(ct);
        foreach (var t in overdue)
        {
            t.OverdueNotifiedAt = now;
            db.Notifications.Add(new Notification { UserId = t.AssigneeUserId!.Value, Title = "Overdue: " + t.Title, TaskId = t.Id, CreatedAt = now,
                Link = t.SubjectType == "lead" ? $"/crm/leads/{t.SubjectId}" : t.SubjectType == "opportunity" ? $"/crm/deals/{t.SubjectId}" : "/inbox" });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { /* another sweep got there first */ }
    }
}

/// <summary>Enqueues a sweep every five minutes. The idempotency key per time bucket means several hosts running this still produce one job.</summary>
public sealed class SweepScheduler(IServiceScopeFactory scopes, IClock clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var bucket = clock.UtcNow.ToUnixTimeSeconds() / 300;
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest(OverdueSweepHandler.SweepJob, "{}", IdempotencyKey: $"sweep:{bucket}"), stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { /* try again next tick */ }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
