using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.Modules.Portfolio.Domain;
using TriSoftic.GrowthEngine.Modules.Portfolio.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Portfolio;

public static class PortfolioModule
{
    public static IServiceCollection AddPortfolioModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<PortfolioDbContext>(c, PortfolioDbContext.Schema);
        s.AddScoped<IApprovalSubjectHandler, ProfileApprovalHandler>();
        s.AddScoped<IProductKnowledge, ProductKnowledge>();
        return s;
    }

    /// <summary>Seeds the categories named in PRD 6.1/6.2. Idempotent.</summary>
    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortfolioDbContext>();
        (string Name, ProductKind Kind)[] wanted =
        [
            ("Android Game", ProductKind.Game),
            ("Cash-and-Carry POS", ProductKind.BusinessSoftware),
            ("Pharmacy POS", ProductKind.BusinessSoftware),
            ("Clothing Shop POS", ProductKind.BusinessSoftware),
            ("Hardware Shop POS", ProductKind.BusinessSoftware),
        ];
        var existing = (await db.Categories.Select(x => x.Name).ToListAsync(ct)).ToHashSet();
        foreach (var (name, kind) in wanted.Where(w => !existing.Contains(w.Name)))
            db.Categories.Add(new ProductCategory { Name = name, Kind = kind });
        await db.SaveChangesAsync(ct);
    }
}
