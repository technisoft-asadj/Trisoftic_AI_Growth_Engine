using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Strategy;

public static class StrategyModule
{
    public static IServiceCollection AddStrategyModule(this IServiceCollection s, IConfiguration c) => s.AddModuleDbContext<StrategyDbContext>(c, StrategyDbContext.Schema);

    public sealed record CreateRequest(string Name, string? TemplateKey, StrategyContent? Content, Guid? SourceDraftId);
    public sealed record SaveVersionRequest(StrategyContent Content, string? Note, int BaseVersion);

    public static IEndpointRouteBuilder MapStrategyEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Strategy");

        g.MapGet("/strategy-templates", (ICurrentUser u) => u.HasPermission(Permissions.ProductsRead)
            ? Results.Ok(StrategyTemplates.All.Select(t => new { t.Key, t.Name, t.Description, starter = t.Starter }))
            : Results.Forbid());

        g.MapGet("/products/{productId:guid}/strategies", async (Guid productId, StrategyDbContext db, ICurrentUser u, bool? includeArchived, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var q = db.Strategies.AsNoTracking().Where(s => s.ProductId == productId);
            if (includeArchived != true) q = q.Where(s => !s.IsArchived);
            return Results.Ok(await q.OrderByDescending(s => s.UpdatedAt).Select(s => new { s.Id, s.Name, s.TemplateKey, s.CurrentVersion, s.IsArchived, s.UpdatedAt }).ToListAsync(ct));
        });

        g.MapPost("/products/{productId:guid}/strategies", async (Guid productId, CreateRequest req, StrategyDbContext db, ICurrentUser u, IAiDraftLookup drafts, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) return Results.Problem(statusCode: 400, title: "A name (max 200 characters) is required");
            var template = StrategyTemplates.All.FirstOrDefault(t => t.Key == (req.TemplateKey ?? "blank"));
            if (template is null) return Results.Problem(statusCode: 400, title: "Unknown template");

            var content = req.Content ?? StrategyContent.FromJson(template.Starter.ToJson());   // deep copy of the starter
            Guid? sourceDraft = null;
            if (req.SourceDraftId is { } draftId)
            {
                var d = await drafts.GetAsync(draftId, productId, ct);
                if (d is null) return Results.Problem(statusCode: 400, title: "The AI draft was not found for this product");
                if (string.IsNullOrWhiteSpace(content.Notes)) content.Notes = d.Body;
                sourceDraft = d.Id;
            }
            var errors = content.Validate();
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = [.. errors] });

            var now = clock.UtcNow;
            var doc = new StrategyDoc { ProductId = productId, Name = req.Name.Trim(), TemplateKey = template.Key, CurrentVersion = 1, CreatedBy = u.UserId, CreatedAt = now, UpdatedAt = now };
            db.Strategies.Add(doc);
            db.Versions.Add(new StrategyVersion { StrategyId = doc.Id, Number = 1, ContentJson = content.ToJson(), Note = sourceDraft is null ? "Created" : "Created from AI draft", SourceDraftId = sourceDraft, CreatedBy = u.UserId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("strategy.created", "Strategy", doc.Id.ToString(), productId, doc.Name, After: new { doc.TemplateKey, SourceDraftId = sourceDraft }), ct);
            return Results.Created($"/api/v1/strategies/{doc.Id}", new { doc.Id, doc.CurrentVersion });
        });

        g.MapGet("/strategies/{id:guid}", async (Guid id, StrategyDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var doc = await db.Strategies.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (doc is null || !u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(doc.ProductId, ct)) return Results.NotFound();
            var versions = await db.Versions.AsNoTracking().Where(v => v.StrategyId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
            return Results.Ok(new
            {
                doc.Id, doc.ProductId, doc.Name, doc.TemplateKey, doc.CurrentVersion, doc.IsArchived,
                current = versions.Where(v => v.Number == doc.CurrentVersion).Select(v => StrategyContent.FromJson(v.ContentJson)).FirstOrDefault(),
                versions = versions.Select(v => new { v.Number, v.Note, v.SourceDraftId, v.CreatedBy, v.CreatedAt }),
            });
        });

        g.MapGet("/strategies/{id:guid}/versions/{n:int}", async (Guid id, int n, StrategyDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var doc = await db.Strategies.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (doc is null || !u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(doc.ProductId, ct)) return Results.NotFound();
            var v = await db.Versions.AsNoTracking().FirstOrDefaultAsync(x => x.StrategyId == id && x.Number == n, ct);
            return v is null ? Results.NotFound() : Results.Ok(new { v.Number, v.Note, v.SourceDraftId, v.CreatedAt, content = StrategyContent.FromJson(v.ContentJson) });
        });

        // Every save creates a NEW immutable version. BaseVersion guards against two people overwriting each other.
        g.MapPost("/strategies/{id:guid}/versions", async (Guid id, SaveVersionRequest req, StrategyDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var doc = await db.Strategies.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (doc is null || !u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(doc.ProductId, ct)) return Results.NotFound();
            if (doc.IsArchived) return Results.Problem(statusCode: 409, title: "Archived strategies cannot be edited");
            if (req.BaseVersion != doc.CurrentVersion) return Results.Problem(statusCode: 409, title: $"Someone saved version {doc.CurrentVersion} since you opened version {req.BaseVersion}. Reload and merge your changes.");
            var errors = req.Content.Validate();
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = [.. errors] });

            var now = clock.UtcNow;
            var next = doc.CurrentVersion + 1;
            db.Versions.Add(new StrategyVersion { StrategyId = id, Number = next, ContentJson = req.Content.ToJson(), Note = req.Note, CreatedBy = u.UserId, CreatedAt = now });
            doc.CurrentVersion = next; doc.UpdatedAt = now;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "Concurrent save detected. Reload and try again."); }
            await audit.WriteAsync(new AuditEntry("strategy.version-saved", "Strategy", id.ToString(), doc.ProductId, $"{doc.Name} v{next}", After: new { req.Note }), ct);
            return Results.Ok(new { doc.Id, doc.CurrentVersion });
        });

        g.MapPost("/strategies/{id:guid}/archive/{archived:bool}", async (Guid id, bool archived, StrategyDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var doc = await db.Strategies.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (doc is null || !u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(doc.ProductId, ct)) return Results.NotFound();
            doc.IsArchived = archived;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry(archived ? "strategy.archived" : "strategy.restored", "Strategy", id.ToString(), doc.ProductId, doc.Name), ct);
            return Results.NoContent();
        });

        return app;
    }
}
