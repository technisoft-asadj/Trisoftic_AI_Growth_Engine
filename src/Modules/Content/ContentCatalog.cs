using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Content;

public sealed class ContentCatalog(ContentDbContext db) : IContentCatalog
{
    public async Task<IReadOnlyList<PublishedPostTags>> GetPublishedAsync(Guid productId, string? channel, CancellationToken ct = default)
        => await db.Items.AsNoTracking().Where(i => i.ProductId == productId && i.Status == ContentStatus.Published && i.PublishedAt != null && (channel == null || i.Channel.ToLower() == channel.ToLower()))
            .Select(i => new PublishedPostTags(i.Id, i.Channel, i.Format, i.HookType, i.PublishedAt!.Value)).ToListAsync(ct);
}
