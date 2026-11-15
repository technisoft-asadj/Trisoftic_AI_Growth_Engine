using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Automation;

public static partial class AutomationModule
{
    public sealed record WorkflowIn(string Name, string Trigger, Guid? ProductId, WorkflowDefinition Definition, bool? Enabled, string? Note);
    public sealed record WorkflowUpdate(string? Name, WorkflowDefinition Definition, string? Note, int BaseVersion);
    public sealed record TaskIn(string Title, string? Notes, Guid? AssigneeUserId, DateTimeOffset? DueAt, Guid? ProductId, string? SubjectType, Guid? SubjectId);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex NameRx();

    /// <param name="runScheduler">Hosts that run background jobs set this so overdue-task alerts are raised. Several hosts doing so is harmless.</param>
    public static IServiceCollection AddAutomationModule(this IServiceCollection s, IConfiguration c, bool runScheduler)
    {
        s.AddModuleDbContext<AutomationDbContext>(c, AutomationDbContext.Schema);
        s.AddHttpClient("automation-webhooks", h => h.Timeout = TimeSpan.FromSeconds(15));
        s.AddScoped<IWebhookClient, WebhookClient>();
        s.AddScoped<WorkflowEngine>();
        s.AddScoped<IEventPublisher, JobEventPublisher>();
        s.AddScoped<IJobHandler, AutomationEventHandler>();
        s.AddScoped<IJobHandler, AutomationResumeHandler>();
        s.AddScoped<IJobHandler, OverdueSweepHandler>();
        if (runScheduler || c.GetValue<bool>("Jobs:RunInProcess")) s.AddHostedService<SweepScheduler>();
        return s;
    }

    private static async Task<bool> CanUseScopeAsync(ICurrentUser u, Guid? productId, CancellationToken ct)
        => productId is { } p ? await u.CanAccessProductAsync(p, ct) : await u.AccessibleProductIdsAsync(ct) is null;   // a workflow for every product needs access to every product

    private static object WorkflowDto(Workflow w) => new { w.Id, w.Name, w.Trigger, w.ProductId, w.Enabled, w.CurrentVersion, w.UpdatedAt };

    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        MapWorkflows(app.MapGroup("/api/v1/automation").RequireAuthorization().WithTags("Automation"));
        MapWork(app.MapGroup("/api/v1").RequireAuthorization().WithTags("Tasks and notifications"));
        MapInboundWebhook(app);
        return app;
    }

    // ---------- workflows ----------
    private static void MapWorkflows(RouteGroupBuilder g)
    {
        g.MapGet("/triggers", (ICurrentUser u, IWebhookClient hooks, IConfiguration cfg) => u.HasPermission(Permissions.AutomationManage)
            ? Results.Ok(new
            {
                triggers = Definitions.Triggers.Select(t => new { type = t.Key, fields = t.Value }),
                externalPrefix = "external.", stepKinds = Definitions.StepKinds.Where(k => k != "email"), ops = Definitions.Ops,
                webhooks = (cfg.GetSection("Automation:Webhooks").GetChildren().Select(x => x.Key)).Where(hooks.IsConfigured),
            })
            : Results.Forbid());

        g.MapGet("/workflows", async (AutomationDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AutomationManage)) return Results.Forbid();
            var scope = await u.AccessibleProductIdsAsync(ct);
            var q = db.Workflows.AsNoTracking().AsQueryable();
            if (scope is not null) q = q.Where(w => w.ProductId == null || scope.Contains(w.ProductId.Value));
            return Results.Ok((await q.OrderBy(w => w.Name).ToListAsync(ct)).Select(WorkflowDto));
        });

        g.MapGet("/workflows/{id:guid}", async (Guid id, AutomationDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var w = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (w is null || !u.HasPermission(Permissions.AutomationManage) || (w.ProductId is { } p && !await u.CanAccessProductAsync(p, ct))) return Results.NotFound();
            var versions = await db.Versions.AsNoTracking().Where(v => v.WorkflowId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
            return Results.Ok(new
            {
                workflow = WorkflowDto(w),
                definition = WorkflowDefinition.FromJson(versions.First(v => v.Number == w.CurrentVersion).DefinitionJson),
                versions = versions.Select(v => new { v.Number, v.Note, v.CreatedAt }),
            });
        });

        g.MapPost("/workflows", async (WorkflowIn req, AutomationDbContext db, ICurrentUser u, IWebhookClient hooks, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AutomationManage) || !await CanUseScopeAsync(u, req.ProductId, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) return Results.Problem(statusCode: 400, title: "A name is required");
            if (!Definitions.IsKnownTrigger(req.Trigger ?? "")) return Results.Problem(statusCode: 400, title: "Unknown trigger");
            var errors = Definitions.Validate(req.Definition ?? new(), hooks.IsConfigured, emailAvailable: false);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [.. errors] });

            var now = clock.UtcNow;
            var w = new Workflow { ProductId = req.ProductId, Name = req.Name.Trim(), Trigger = req.Trigger!, Enabled = req.Enabled ?? false, CurrentVersion = 1, CreatedBy = u.UserId, CreatedAt = now, UpdatedAt = now };
            db.Workflows.Add(w);
            db.Versions.Add(new WorkflowVersion { WorkflowId = w.Id, Number = 1, DefinitionJson = req.Definition!.ToJson(), Note = req.Note ?? "Created", CreatedBy = u.UserId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("automation.workflow.created", "Workflow", w.Id.ToString(), w.ProductId, w.Name, After: new { w.Trigger, w.Enabled, Steps = req.Definition.Steps.Count }), ct);
            return Results.Created($"/api/v1/automation/workflows/{w.Id}", WorkflowDto(w));
        });

        // A change is a new immutable version; runs already in flight keep the version they started with.
        g.MapPut("/workflows/{id:guid}", async (Guid id, WorkflowUpdate req, AutomationDbContext db, ICurrentUser u, IWebhookClient hooks, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var w = await db.Workflows.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (w is null || !u.HasPermission(Permissions.AutomationManage) || !await CanUseScopeAsync(u, w.ProductId, ct)) return Results.NotFound();
            if (req.BaseVersion != w.CurrentVersion) return Results.Problem(statusCode: 409, title: $"Someone saved version {w.CurrentVersion} meanwhile; reload and re-apply your change");
            var errors = Definitions.Validate(req.Definition ?? new(), hooks.IsConfigured, emailAvailable: false);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [.. errors] });
            var now = clock.UtcNow;
            w.CurrentVersion++; w.UpdatedAt = now; if (!string.IsNullOrWhiteSpace(req.Name)) w.Name = req.Name.Trim();
            db.Versions.Add(new WorkflowVersion { WorkflowId = id, Number = w.CurrentVersion, DefinitionJson = req.Definition!.ToJson(), Note = req.Note, CreatedBy = u.UserId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("automation.workflow.updated", "Workflow", id.ToString(), w.ProductId, w.Name, After: new { Version = w.CurrentVersion, Steps = req.Definition.Steps.Count }), ct);
            return Results.Ok(WorkflowDto(w));
        });

        foreach (var (route, enable) in new[] { ("enable", true), ("disable", false) })
            g.MapPost($"/workflows/{{id:guid}}/{route}", async (Guid id, AutomationDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
            {
                var w = await db.Workflows.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (w is null || !u.HasPermission(Permissions.AutomationManage) || !await CanUseScopeAsync(u, w.ProductId, ct)) return Results.NotFound();
                if (w.Enabled != enable)
                {
                    w.Enabled = enable; w.UpdatedAt = clock.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await audit.WriteAsync(new AuditEntry(enable ? "automation.workflow.enabled" : "automation.workflow.paused", "Workflow", id.ToString(), w.ProductId, w.Name), ct);
                }
                return Results.Ok(WorkflowDto(w));
            });

        g.MapGet("/workflows/{id:guid}/runs", async (Guid id, AutomationDbContext db, ICurrentUser u, int? take, CancellationToken ct) =>
        {
            var w = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (w is null || !u.HasPermission(Permissions.AutomationManage) || (w.ProductId is { } p && !await u.CanAccessProductAsync(p, ct))) return Results.NotFound();
            var runs = await db.Runs.AsNoTracking().Where(r => r.WorkflowId == id).OrderByDescending(r => r.Id).Take(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct);
            return Results.Ok(runs.Select(r => new { r.Id, r.VersionNumber, r.EventType, r.SubjectType, r.SubjectId, r.Status, r.NextStep, r.ResumeAt, r.Error, r.CreatedAt, r.CompletedAt }));
        });

        g.MapGet("/runs/{id:guid}", async (Guid id, AutomationDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var r = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            var w = r is null ? null : await db.Workflows.AsNoTracking().FirstAsync(x => x.Id == r.WorkflowId, ct);
            if (r is null || w is null || !u.HasPermission(Permissions.AutomationManage) || (w.ProductId is { } p && !await u.CanAccessProductAsync(p, ct))) return Results.NotFound();
            var logs = await db.Logs.AsNoTracking().Where(l => l.RunId == id).OrderBy(l => l.At).ThenBy(l => l.Id).ToListAsync(ct);
            return Results.Ok(new
            {
                run = new { r.Id, workflow = w.Name, r.VersionNumber, r.EventType, r.SubjectType, r.SubjectId, r.Status, r.NextStep, r.ResumeAt, r.Error, r.CreatedAt, r.CompletedAt },
                steps = logs.Select(l => new { l.StepIndex, l.Kind, l.Status, l.Detail, l.At }),
            });
        });
    }

    // ---------- tasks and notifications ----------
    private static object TaskDto(WorkTask t, DateTimeOffset now) => new
    {
        t.Id, t.Title, t.Notes, t.AssigneeUserId, t.DueAt, t.CompletedAt, t.SubjectType, t.SubjectId, t.ProductId, t.CreatedAt,
        overdue = t.CompletedAt == null && t.DueAt != null && t.DueAt < now,
    };

    private static void MapWork(RouteGroupBuilder g)
    {
        g.MapGet("/tasks", async (AutomationDbContext db, ICurrentUser u, IClock clock, string? scope, bool? includeDone, Guid? subjectId, CancellationToken ct) =>
        {
            if (!u.IsAuthenticated) return Results.Unauthorized();
            var q = db.Tasks.AsNoTracking().AsQueryable();
            if (scope == "all")
            {
                if (!u.HasPermission(Permissions.AutomationManage)) return Results.Forbid();
                var ids = await u.AccessibleProductIdsAsync(ct);
                if (ids is not null) q = q.Where(t => t.ProductId == null || ids.Contains(t.ProductId.Value));
            }
            else q = q.Where(t => t.AssigneeUserId == u.UserId);
            if (subjectId is not null) q = q.Where(t => t.SubjectId == subjectId);
            if (includeDone != true) q = q.Where(t => t.CompletedAt == null);
            var now = clock.UtcNow;
            var rows = await q.OrderBy(t => t.CompletedAt != null).ThenBy(t => t.DueAt == null).ThenBy(t => t.DueAt).Take(300).ToListAsync(ct);
            return Results.Ok(rows.Select(t => TaskDto(t, now)));
        });

        g.MapPost("/tasks", async (TaskIn req, AutomationDbContext db, ICurrentUser u, IUserDirectory dir, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmWrite)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 300) return Results.Problem(statusCode: 400, title: "A title is required");
            if (req.ProductId is { } pid && !await u.CanAccessProductAsync(pid, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inaccessible product");
            var assignee = req.AssigneeUserId ?? u.UserId;
            if (assignee is { } a && !await dir.IsActiveUserAsync(a, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inactive assignee");
            var now = clock.UtcNow;
            var t = new WorkTask { Title = req.Title.Trim(), Notes = req.Notes, AssigneeUserId = assignee, DueAt = req.DueAt, ProductId = req.ProductId, SubjectType = req.SubjectType, SubjectId = req.SubjectId, CreatedBy = u.UserId, CreatedAt = now };
            db.Tasks.Add(t);
            if (assignee is { } who && who != u.UserId) db.Notifications.Add(new Notification { UserId = who, Title = "New task: " + t.Title, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/tasks/{t.Id}", TaskDto(t, now));
        });

        g.MapPost("/tasks/{id:guid}/complete", async (Guid id, AutomationDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var t = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null || !u.IsAuthenticated) return Results.NotFound();
            if (t.AssigneeUserId != u.UserId && !u.HasPermission(Permissions.AutomationManage)) return Results.NotFound();
            t.CompletedAt ??= clock.UtcNow; t.CompletedBy ??= u.UserId;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/notifications", async (AutomationDbContext db, ICurrentUser u, bool? unread, CancellationToken ct) =>
        {
            if (!u.IsAuthenticated) return Results.Unauthorized();
            var q = db.Notifications.AsNoTracking().Where(n => n.UserId == u.UserId);
            if (unread == true) q = q.Where(n => n.ReadAt == null);
            return Results.Ok(await q.OrderByDescending(n => n.Id).Take(100).Select(n => new { n.Id, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt }).ToListAsync(ct));
        });

        g.MapPost("/notifications/{id:guid}/read", async (Guid id, AutomationDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId, ct);
            if (n is null) return Results.NotFound();
            n.ReadAt ??= clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/notifications/read-all", async (AutomationDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            await db.Notifications.Where(n => n.UserId == u.UserId && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
            return Results.NoContent();
        });

        // The numbers behind the "overdue" badge in the app shell.
        g.MapGet("/me/work-summary", async (AutomationDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            if (!u.IsAuthenticated) return Results.Unauthorized();
            var now = clock.UtcNow;
            var open = db.Tasks.AsNoTracking().Where(t => t.AssigneeUserId == u.UserId && t.CompletedAt == null);
            return Results.Ok(new
            {
                openTasks = await open.CountAsync(ct),
                overdueTasks = await open.CountAsync(t => t.DueAt != null && t.DueAt < now, ct),
                unreadNotifications = await db.Notifications.CountAsync(n => n.UserId == u.UserId && n.ReadAt == null, ct),
            });
        });
    }

    // ---------- signed inbound webhook (n8n -> platform) ----------
    private sealed record InboundBody(string? EventType, string? SubjectType, Guid? SubjectId, Guid? ProductId, Dictionary<string, string?>? Data);

    private static void MapInboundWebhook(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/webhooks/automation/{name}", async (string name, HttpRequest req, AutomationDbContext db, IConfiguration cfg, IClock clock, IEventPublisher events, CancellationToken ct) =>
        {
            var secret = NameRx().IsMatch(name) ? cfg[$"Automation:Webhooks:{name}:Secret"] : null;
            if (string.IsNullOrEmpty(secret)) return Results.NotFound();           // unknown names look the same as anything else
            if (req.ContentLength is null or > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            string body; using (var sr = new StreamReader(req.Body)) body = await sr.ReadToEndAsync(ct);
            var signature = req.Headers["X-TE-Signature"].ToString();
            if (!WebhookSigner.Verify(secret, req.Headers["X-TE-Timestamp"].ToString(), body, signature, clock.UtcNow)) return Results.Unauthorized();

            // Inside the five-minute window a captured request could be sent again; each signature is accepted once.
            db.Receipts.Add(new WebhookReceipt { Name = name, SignatureHash = WebhookSigner.Hash(signature), ReceivedAt = clock.UtcNow });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { return Results.Problem(statusCode: 409, title: "This request was already received"); }

            InboundBody? b;
            try { b = JsonSerializer.Deserialize<InboundBody>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch (JsonException) { b = null; }
            if (b is null || b.SubjectId is null || string.IsNullOrWhiteSpace(b.SubjectType) || b.EventType is null || !Regex.IsMatch(b.EventType, "^[a-z0-9._-]{1,50}$"))
                return Results.Problem(statusCode: 400, title: "eventType (lowercase), subjectType and subjectId are required");
            await events.PublishAsync(new PlatformEvent("external." + b.EventType, b.ProductId, b.SubjectType, b.SubjectId.Value, b.Data), ct);
            return Results.Accepted();
        }).AllowAnonymous().WithTags("Webhooks");
    }
}
