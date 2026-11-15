using Microsoft.EntityFrameworkCore;
using Serilog.Context;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Ai;
using TriSoftic.GrowthEngine.Modules.Assets;
using TriSoftic.GrowthEngine.Modules.Content;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;
using TriSoftic.GrowthEngine.Modules.Identity;
using TriSoftic.GrowthEngine.Modules.Identity.Persistence;
using TriSoftic.GrowthEngine.Modules.Jobs;
using TriSoftic.GrowthEngine.Modules.Portfolio;
using TriSoftic.GrowthEngine.Modules.Portfolio.Persistence;
using TriSoftic.GrowthEngine.Modules.Strategy;

namespace TriSoftic.GrowthEngine.Host.Api;

/// <summary>Reads or creates the correlation id and exposes it to logs, the response, and downstream calls.</summary>
public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext ctx, ICorrelationContext corr)
    {
        var incoming = ctx.Request.Headers[Header].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64 && incoming.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            corr.CorrelationId = incoming;
        ctx.Response.Headers[Header] = corr.CorrelationId;
        using (LogContext.PushProperty("CorrelationId", corr.CorrelationId))
            await next(ctx);
    }
}

/// <summary>Applies pending migrations for every module on startup when Database:MigrateOnStartup=true, then seeds identity.</summary>
public sealed class DatabaseMigrationHostedService(IServiceProvider sp, IConfiguration config, ILogger<DatabaseMigrationHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!config.GetValue<bool>("Database:MigrateOnStartup")) { log.LogInformation("Migrate-on-startup disabled"); return; }

        // Retry: in compose the database may still be starting.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = sp.CreateScope();
                foreach (var ctxType in ModuleContexts.All)
                {
                    var db = (DbContext)scope.ServiceProvider.GetRequiredService(ctxType);
                    await db.Database.MigrateAsync(ct);
                    log.LogInformation("Migrated {Context}", ctxType.Name);
                }
                await IdentitySeeder.SeedAsync(sp, ct);
                await PortfolioModule.SeedAsync(sp, ct);
                return;
            }
            catch (Exception ex) when (attempt < 10 && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Migration attempt {Attempt} failed; retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Every module DbContext, in dependency order. Add new module contexts here.</summary>
public static class ModuleContexts
{
    public static readonly Type[] All =
    [
        typeof(GovernanceDbContext),
        typeof(IdentityModuleDbContext),
        typeof(JobsDbContext),
        typeof(PortfolioDbContext),
        typeof(AiDbContext),
        typeof(AssetsDbContext),
        typeof(StrategyDbContext),
        typeof(ContentDbContext),
    ];
}

public static class PlatformRegistration
{
    /// <summary>Modules beyond Identity/Governance register here as they are added.</summary>
    public static IServiceCollection AddPlatformModules(this IServiceCollection s, IConfiguration c)
    {
        s.AddJobsModule(c, runWorker: false);
        s.AddPortfolioModule(c);
        s.AddAiModule(c);
        s.AddAssetsModule(c);
        s.AddStrategyModule(c);
        s.AddContentModule(c);
        return s;
    }

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapJobsEndpoints();
        app.MapPortfolioEndpoints();
        app.MapAiEndpoints();
        app.MapAssetsEndpoints();
        app.MapStrategyEndpoints();
        app.MapContentEndpoints();
        return app;
    }
}


