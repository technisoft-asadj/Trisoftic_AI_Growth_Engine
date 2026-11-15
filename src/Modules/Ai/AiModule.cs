using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Ai;

public static class AiModule
{
    public static IServiceCollection AddAiModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<AiDbContext>(c, AiDbContext.Schema);
        s.Configure<AiOptions>(c.GetSection("Ai"));
        s.AddHttpClient("openai", h => h.Timeout = TimeSpan.FromMinutes(5));
        s.AddSingleton<IAiProvider, AnthropicProvider>();
        s.AddSingleton<IAiProvider, AnthropicBedrockProvider>();
        s.AddScoped<IAiProvider, OpenAiProvider>();
        s.AddScoped<IAiGenerator, AiGenerator>();
        s.AddScoped<GroundedGenerator>();
        s.AddScoped<IAiDraftLookup, AiDraftLookup>();
        return s;
    }

    public sealed record GenerateBody(GroundedKind Kind, string Brief, string? Channel, string? Language, string? Audience);
    public sealed record UsageSummary(decimal MonthToDateUsd, decimal GlobalBudgetUsd, decimal PerProductBudgetUsd, bool GlobalAlert, IReadOnlyList<ProductUsage> Products);
    public sealed record ProductUsage(Guid? ProductId, decimal CostUsd, int Calls, bool Alert, bool Exhausted);

    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("AI");

        g.MapGet("/products/{id:guid}/ai/drafts", async (Guid id, AiDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var rows = await db.Drafts.AsNoTracking().Where(d => d.ProductId == id).OrderByDescending(d => d.Id).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(d => new { d.Id, d.Kind, d.Title, d.Channel, d.Language, d.ProfileVersion, d.Provider, d.Model, d.CostUsd, d.CreatedAt }));
        });

        g.MapGet("/ai/drafts/{id:guid}", async (Guid id, AiDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var d = await db.Drafts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null || !u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(d.ProductId, ct)) return Results.NotFound();
            return Results.Ok(new
            {
                d.Id, d.ProductId, d.Kind, d.Brief, d.Channel, d.Language, d.Title, d.Body,
                claims = System.Text.Json.JsonDocument.Parse(d.ClaimsJson).RootElement, questions = System.Text.Json.JsonDocument.Parse(d.QuestionsJson).RootElement,
                d.ProfileVersion, d.Provider, d.Model, d.TemplateKey, d.TemplateVersion, d.CostUsd, d.CreatedAt,
            });
        });

        g.MapPost("/products/{id:guid}/ai/generate", async (Guid id, GenerateBody body, GroundedGenerator gen, AiDbContext db, IClock clock, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AiGenerate) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Brief) || body.Brief.Length > 4000) return Results.Problem(statusCode: 400, title: "A brief of up to 4000 characters is required");
            try
            {
                var res = await gen.GenerateAsync(new GroundedRequest(id, body.Kind, body.Brief, body.Channel, body.Language, body.Audience), ct);
                if (!res.IsSuccess)
                {
                    var e = res.Error!;
                    var status = e.Kind switch { ErrorKind.Conflict => 409, ErrorKind.Policy => 422, _ => 400 };
                    return Results.Problem(statusCode: status, title: e.Message, extensions: new Dictionary<string, object?> { ["code"] = e.Code });
                }
                var o = res.Value!;
                var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
                var saved = new AiDraft
                {
                    ProductId = id, Kind = body.Kind.ToString(), Brief = body.Brief, Channel = body.Channel, Language = body.Language,
                    Title = o.Draft.Title, Body = o.Draft.Body, ClaimsJson = System.Text.Json.JsonSerializer.Serialize(o.Draft.Claims, json),
                    QuestionsJson = System.Text.Json.JsonSerializer.Serialize(o.Draft.Questions, json), ProfileVersion = o.ProfileVersion,
                    Provider = o.Provider, Model = o.Model, TemplateKey = o.TemplateKey, TemplateVersion = o.TemplateVersion, CostUsd = o.CostUsd,
                    CreatedBy = u.UserId, CreatedAt = clock.UtcNow,
                };
                db.Drafts.Add(saved);
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(new AuditEntry("ai.generated", "AiDraft", saved.Id.ToString(), id, $"{o.TemplateKey} v{o.TemplateVersion}",
                    After: new { o.Provider, o.Model, o.CostUsd, o.ProfileVersion, o.Attempts }), ct);
                return Results.Ok(new { draftId = saved.Id, o.Draft, o.ProfileVersion, o.Provider, o.Model, o.CostUsd, o.Attempts, o.TemplateKey, o.TemplateVersion });
            }
            catch (AiException ex)
            {
                var status = ex.Outcome switch { AiOutcome.BudgetBlocked => 402, AiOutcome.Paused => 503, AiOutcome.Refused => 422, _ => 502 };
                return Results.Problem(statusCode: status, title: ex.Message, extensions: new Dictionary<string, object?> { ["outcome"] = ex.Outcome.ToString() });
            }
        });

        g.MapGet("/ai/usage", async (AiDbContext db, ICurrentUser u, Microsoft.Extensions.Options.IOptions<AiOptions> opt, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.BudgetRead)) return Results.Forbid();
            var now = clock.UtcNow; var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var rows = await db.Usage.AsNoTracking().Where(x => x.OccurredAt >= start && x.Outcome == AiOutcome.Succeeded)
                .GroupBy(x => x.ProductId).Select(x => new { ProductId = x.Key, Cost = x.Sum(y => y.EstimatedCostUsd), Calls = x.Count() }).ToListAsync(ct);
            var o = opt.Value; var total = rows.Sum(r => r.Cost);
            return Results.Ok(new UsageSummary(total, o.MonthlyBudgetUsdGlobal, o.MonthlyBudgetUsdPerProduct, total >= o.MonthlyBudgetUsdGlobal * o.AlertThreshold,
                rows.Select(r => new ProductUsage(r.ProductId, r.Cost, r.Calls, r.Cost >= o.MonthlyBudgetUsdPerProduct * o.AlertThreshold, r.Cost >= o.MonthlyBudgetUsdPerProduct)).ToList()));
        });

        return app;
    }
}

public sealed class AiDraftLookup(AiDbContext db) : IAiDraftLookup
{
    public async Task<DraftSummary?> GetAsync(Guid draftId, Guid productId, CancellationToken ct = default) =>
        await db.Drafts.AsNoTracking().Where(d => d.Id == draftId && d.ProductId == productId)
            .Select(d => new DraftSummary(d.Id, d.ProductId, d.Kind, d.Title, d.Body, d.ProfileVersion, d.Provider, d.Model, d.CreatedAt)).FirstOrDefaultAsync(ct);
}
