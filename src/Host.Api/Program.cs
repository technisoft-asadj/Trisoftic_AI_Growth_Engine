using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Host.Api;
using TriSoftic.GrowthEngine.Modules.Governance;
using TriSoftic.GrowthEngine.Modules.Identity;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, sp, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter()));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<ICorrelationContext, CorrelationContext>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddGovernanceModule(builder.Configuration);
builder.Services.AddPlatformModules(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Default")!, name: "postgres", tags: ["ready"]);

builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddHostedService<DatabaseMigrationHostedService>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationMiddleware>();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseCsrfHeaderGuard();
app.UseAuthorization();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });

app.MapIdentityEndpoints();
app.MapGovernanceEndpoints();
app.MapPlatformEndpoints();

await app.RunAsync();

public partial class Program;
