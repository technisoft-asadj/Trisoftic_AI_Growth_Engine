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

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 210L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 210L * 1024 * 1024);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<ICorrelationContext, CorrelationContext>();
builder.Services.AddProblemDetails();
// Malformed request bodies must answer 400 in every environment (Development defaults to throwing, which surfaced as 500).
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();

builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddGovernanceModule(builder.Configuration);
builder.Services.AddPlatformModules(builder.Configuration);

// Anonymous lead forms are throttled per form key + client address (default 20 submissions a minute; RateLimits:PublicFormsPerMinute).
var formsPerMinute = builder.Configuration.GetValue<int?>("RateLimits:PublicFormsPerMinute") ?? 20;
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(TriSoftic.GrowthEngine.Modules.Crm.CrmModule.PublicFormsPolicy, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        $"{ctx.Request.RouteValues["key"]}|{ctx.Connection.RemoteIpAddress}",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = formsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Anonymous unsubscribe links: per client address.
    o.AddPolicy(TriSoftic.GrowthEngine.Modules.Messaging.MessagingModule.PublicPolicy, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        $"unsub|{ctx.Connection.RemoteIpAddress}",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Anonymous booking pages: per booking page (or manage token) + client address.
    var bookingPerMinute = builder.Configuration.GetValue<int?>("RateLimits:PublicBookingPerMinute") ?? 60;
    o.AddPolicy(TriSoftic.GrowthEngine.Modules.Scheduling.SchedulingModule.PublicPolicy, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        $"{ctx.Request.RouteValues["key"] ?? ctx.Request.RouteValues["token"]}|{ctx.Connection.RemoteIpAddress}",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = bookingPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Default")!, name: "postgres", tags: ["ready"]);

builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddHostedService<DatabaseMigrationHostedService>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationMiddleware>();
app.UseCors();
app.UseRateLimiter();
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

