using Serilog;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Automation;
using TriSoftic.GrowthEngine.Modules.Content;
using TriSoftic.GrowthEngine.Modules.Crm;
using TriSoftic.GrowthEngine.Modules.Publishing;
using TriSoftic.GrowthEngine.Modules.Governance;
using TriSoftic.GrowthEngine.Modules.Scheduling;
using TriSoftic.GrowthEngine.Modules.Jobs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((sp, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter()));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<ICorrelationContext, CorrelationContext>();
builder.Services.AddScoped<ICurrentUser, SystemUser>();   // background work acts as "system": no user, no permissions
builder.Services.AddGovernanceModule(builder.Configuration);   // pause switch + audit
builder.Services.AddJobsModule(builder.Configuration, runWorker: true);
// Modules whose job handlers or state providers the worker needs. Their endpoints live only in the API host.
builder.Services.AddCrmModule(builder.Configuration);
builder.Services.AddSchedulingModule(builder.Configuration);
builder.Services.AddAutomationModule(builder.Configuration, runScheduler: true);
builder.Services.AddContentModule(builder.Configuration);
builder.Services.AddPublishingModule(builder.Configuration, runScheduler: true);
// Job handlers (AI generation, publishing, imports, reminders...) register here as their modules are added.

await builder.Build().RunAsync();

/// <summary>Identity of background jobs. Audit entries written by workers carry a null actor.</summary>
internal sealed class SystemUser : ICurrentUser
{
    public bool IsAuthenticated => false;
    public Guid? UserId => null;
    public string? Email => "system@worker";
    public bool HasPermission(string permission) => false;
    public Task<bool> CanAccessProductAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyCollection<Guid>?> AccessibleProductIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<Guid>?>(null);
}
