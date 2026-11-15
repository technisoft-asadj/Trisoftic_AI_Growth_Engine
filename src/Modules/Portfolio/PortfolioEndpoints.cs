using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Portfolio.Domain;
using TriSoftic.GrowthEngine.Modules.Portfolio.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Portfolio;

public static partial class PortfolioEndpoints
{
    public sealed record CategoryDto(Guid Id, string Name, ProductKind Kind);
    public sealed record ProductDto(Guid Id, string Name, string Slug, Guid CategoryId, ProductKind Kind, Lifecycle Lifecycle, string? Summary,
        Dictionary<string, string> Identifiers, bool IsArchived, int? ApprovedProfileVersion, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record CreateProductRequest(string Name, Guid CategoryId, Lifecycle? Lifecycle, string? Summary, Dictionary<string, string>? Identifiers);
    public sealed record UpdateProductRequest(string? Name, Lifecycle? Lifecycle, string? Summary, Dictionary<string, string>? Identifiers);
    public sealed record VersionDto(Guid Id, Guid ProductId, int Number, ProfileStatus Status, string? ChangeNote, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
        Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment, ProductProfile Content);
    public sealed record SaveDraftRequest(ProductProfile Content, string? ChangeNote);

    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Portfolio");

        g.MapGet("/product-categories", async (PortfolioDbContext db, ICurrentUser u, CancellationToken ct) =>
            u.HasPermission(Permissions.ProductsRead)
                ? Results.Ok((await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(c => new CategoryDto(c.Id, c.Name, c.Kind)))
                : Results.Forbid());

        // ---------- products ----------
        g.MapGet("/products", async (PortfolioDbContext db, ICurrentUser u, bool? includeArchived, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead)) return Results.Forbid();
            var q = db.Products.AsNoTracking().AsQueryable();
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(p => scope.Contains(p.Id));
            if (includeArchived != true) q = q.Where(p => !p.IsArchived);
            return Results.Ok((await q.OrderBy(p => p.Name).ToListAsync(ct)).Select(ToDto));
        });

        g.MapGet("/products/{id:guid}", async (Guid id, PortfolioDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return p is null ? Results.NotFound() : Results.Ok(ToDto(p));
        });

        g.MapPost("/products", async (CreateProductRequest req, PortfolioDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsWrite)) return Results.Forbid();
            if (await u.AccessibleProductIdsAsync(ct) is not null) return Results.Problem(statusCode: 403, title: "Creating products requires access to all products");
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) return Results.Problem(statusCode: 400, title: "Name is required (max 200)");
            var cat = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CategoryId, ct);
            if (cat is null) return Results.Problem(statusCode: 400, title: "Unknown category");
            var now = clock.UtcNow;
            var p = new Product
            {
                Name = req.Name.Trim(), Slug = await UniqueSlugAsync(db, req.Name, ct), CategoryId = cat.Id, Kind = cat.Kind,
                Lifecycle = req.Lifecycle ?? Lifecycle.Idea, Summary = req.Summary, Identifiers = req.Identifiers ?? [], CreatedAt = now, UpdatedAt = now,
            };
            db.Products.Add(p);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("product.created", "Product", p.Id.ToString(), p.Id, p.Name, After: new { p.Name, p.Slug, p.Kind }), ct);
            return Results.Created($"/api/v1/products/{p.Id}", ToDto(p));
        });

        g.MapPut("/products/{id:guid}", async (Guid id, UpdateProductRequest req, PortfolioDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsWrite) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            var before = new { p.Name, p.Lifecycle, p.Summary, p.Identifiers };
            if (!string.IsNullOrWhiteSpace(req.Name)) p.Name = req.Name.Trim();
            if (req.Lifecycle is not null) p.Lifecycle = req.Lifecycle.Value;
            if (req.Summary is not null) p.Summary = req.Summary;
            if (req.Identifiers is not null) p.Identifiers = req.Identifiers;
            p.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("product.updated", "Product", id.ToString(), id, p.Name, Before: before, After: new { p.Name, p.Lifecycle, p.Summary, p.Identifiers }), ct);
            return Results.Ok(ToDto(p));
        });

        g.MapPost("/products/{id:guid}/archive/{archived:bool}", async (Guid id, bool archived, PortfolioDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsWrite) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            p.IsArchived = archived; p.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry(archived ? "product.archived" : "product.unarchived", "Product", id.ToString(), id, p.Name), ct);
            return Results.NoContent();
        });

        // ---------- profile versions ----------
        g.MapGet("/products/{id:guid}/profile/versions", async (Guid id, PortfolioDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var rows = await db.ProfileVersions.AsNoTracking().Where(v => v.ProductId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
            return Results.Ok(rows.Select(ToVersionDto));
        });

        g.MapGet("/products/{id:guid}/profile/versions/{n:int}", async (Guid id, int n, PortfolioDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var v = await db.ProfileVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == id && x.Number == n, ct);
            return v is null ? Results.NotFound() : Results.Ok(ToVersionDto(v));
        });

        g.MapGet("/products/{id:guid}/profile/approved", async (Guid id, PortfolioDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var v = await db.ProfileVersions.AsNoTracking().Where(x => x.ProductId == id && x.Status == ProfileStatus.Approved).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
            return v is null ? Results.NotFound() : Results.Ok(ToVersionDto(v));
        });

        g.MapPost("/products/{id:guid}/profile/versions", async (Guid id, PortfolioDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsWrite) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            if (!await db.Products.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
            if (await db.ProfileVersions.AnyAsync(v => v.ProductId == id && (v.Status == ProfileStatus.Draft || v.Status == ProfileStatus.InReview), ct))
                return Results.Problem(statusCode: 409, title: "An open draft or in-review version already exists");
            // Clone the latest approved version (else latest of any status) so work is never started from scratch.
            var basis = await db.ProfileVersions.AsNoTracking().Where(v => v.ProductId == id)
                .OrderByDescending(v => v.Status == ProfileStatus.Approved).ThenByDescending(v => v.Number).FirstOrDefaultAsync(ct);
            var next = (await db.ProfileVersions.Where(v => v.ProductId == id).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
            var nv = new ProductProfileVersion { ProductId = id, Number = next, ContentJson = basis?.ContentJson ?? new ProductProfile().ToJson(), CreatedBy = u.UserId, CreatedAt = clock.UtcNow, ChangeNote = basis is null ? "Initial draft" : $"Based on v{basis.Number}" };
            db.ProfileVersions.Add(nv);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "An open version already exists"); }
            await audit.WriteAsync(new AuditEntry("product.profile.draft-created", "ProductProfile", id.ToString(), id, $"v{next}"), ct);
            return Results.Created($"/api/v1/products/{id}/profile/versions/{next}", ToVersionDto(nv));
        });

        g.MapPut("/products/{id:guid}/profile/versions/{n:int}", async (Guid id, int n, SaveDraftRequest req, PortfolioDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsWrite) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var v = await db.ProfileVersions.FirstOrDefaultAsync(x => x.ProductId == id && x.Number == n, ct);
            if (v is null) return Results.NotFound();
            if (v.Status != ProfileStatus.Draft) return Results.Problem(statusCode: 409, title: $"Version is {v.Status} and cannot be edited");
            var errors = ProductProfileValidator.Validate(req.Content, forSubmission: false);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = [.. errors] });
            v.ContentJson = req.Content.ToJson();
            if (req.ChangeNote is not null) v.ChangeNote = req.ChangeNote;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("product.profile.draft-saved", "ProductProfile", id.ToString(), id, $"v{n}"), ct);
            return Results.Ok(ToVersionDto(v));
        });

        g.MapPost("/products/{id:guid}/profile/versions/{n:int}/submit", async (Guid id, int n, PortfolioDbContext db, ICurrentUser u, IClock clock, IApprovalService approvals, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsWrite) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var v = await db.ProfileVersions.FirstOrDefaultAsync(x => x.ProductId == id && x.Number == n, ct);
            if (v is null) return Results.NotFound();
            if (v.Status != ProfileStatus.Draft) return Results.Problem(statusCode: 409, title: $"Version is {v.Status}; only drafts can be submitted");
            var profile = ProductProfile.FromJson(v.ContentJson);
            var errors = ProductProfileValidator.Validate(profile, forSubmission: true);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = [.. errors] });
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.Id == id, ct);

            v.ContentJson = profile.ToJson();
            v.Submit(clock.UtcNow);
            await db.SaveChangesAsync(ct);
            var req = await approvals.RequestAsync(new ApprovalRequest(ProfileApprovalHandler.Subject, id.ToString(), n, id, $"{product.Name} profile v{n}", v.ChangeNote), ct);
            if (!req.IsSuccess)
            {
                v.Withdraw(); await db.SaveChangesAsync(ct);   // keep state consistent if the request could not be created
                return Results.Problem(statusCode: 409, title: req.Error!.Message);
            }
            return Results.Ok(ToVersionDto(v));
        });

        g.MapPost("/products/{id:guid}/profile/versions/{n:int}/withdraw", async (Guid id, int n, PortfolioDbContext db, ICurrentUser u, IApprovalService approvals, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsWrite) || !await u.CanAccessProductAsync(id, ct)) return Results.NotFound();
            var v = await db.ProfileVersions.FirstOrDefaultAsync(x => x.ProductId == id && x.Number == n, ct);
            if (v is null) return Results.NotFound();
            if (v.Status != ProfileStatus.InReview) return Results.Problem(statusCode: 409, title: $"Version is {v.Status}; only in-review versions can be withdrawn");
            v.Withdraw();
            await db.SaveChangesAsync(ct);
            await approvals.WithdrawAsync(ProfileApprovalHandler.Subject, id.ToString(), ct);
            return Results.Ok(ToVersionDto(v));
        });

        return app;
    }

    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Slug, p.CategoryId, p.Kind, p.Lifecycle, p.Summary, p.Identifiers, p.IsArchived, p.ApprovedProfileVersion, p.CreatedAt, p.UpdatedAt);

    private static VersionDto ToVersionDto(ProductProfileVersion v) =>
        new(v.Id, v.ProductId, v.Number, v.Status, v.ChangeNote, v.CreatedAt, v.SubmittedAt, v.DecidedBy, v.DecidedAt, v.DecisionComment, ProductProfile.FromJson(v.ContentJson));

    private static async Task<string> UniqueSlugAsync(PortfolioDbContext db, string name, CancellationToken ct)
    {
        var baseSlug = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "product";
        if (baseSlug.Length > 80) baseSlug = baseSlug[..80];
        var slug = baseSlug;
        for (var i = 2; await db.Products.AnyAsync(p => p.Slug == slug, ct); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
