using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Portfolio.Domain;
using TriSoftic.GrowthEngine.Modules.Portfolio.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Portfolio;

/// <summary>
/// The ONLY door through which AI reads product data. Exposes the latest APPROVED profile version and, within it,
/// only facts flagged Verified and claims that have evidence; drafts and unverified notes never reach a prompt.
/// </summary>
public sealed class ProductKnowledge(PortfolioDbContext db) : IProductKnowledge
{
    public async Task<ApprovedKnowledge?> GetApprovedAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && !p.IsArchived, ct);
        if (product is null) return null;
        var v = await db.ProfileVersions.AsNoTracking().Where(x => x.ProductId == productId && x.Status == ProfileStatus.Approved)
            .OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        if (v is null) return null;

        var p = ProductProfile.FromJson(v.ContentJson);
        return new ApprovedKnowledge(
            product.Id, product.Name, product.Kind.ToString(), product.Lifecycle.ToString(), v.Number, p.Summary,
            p.Facts.Where(f => f.Verified).Select(f => new KnowledgeFact(f.Id, f.Category, f.Text)).ToList(),
            p.Claims.Where(c => c.Status == ClaimStatus.Prohibited || !string.IsNullOrWhiteSpace(c.Evidence))
                .Select(c => new KnowledgeClaim(c.Id, c.Text, c.Status == ClaimStatus.Prohibited)).ToList(),
            p.Personas.Select(x => new KnowledgePersona(x.Id, x.Name, x.Description, x.PainPoints)).ToList(),
            p.Markets.Select(m => new KnowledgeMarket(m.Country, m.Languages)).ToList(),
            p.Platforms, p.BrandVoice.Tone, p.BrandVoice.Dos, p.BrandVoice.Donts);
    }
}
