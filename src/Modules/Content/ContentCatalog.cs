using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Content;

public sealed class ContentCatalog(ContentDbContext db) : IContentCatalog
{
    public async Task<IReadOnlyList<PublishedPostTags>> GetPublishedAsync(Guid productId, string? channel, CancellationToken ct = default)
        => await db.Items.AsNoTracking().Where(i => i.ProductId == productId && i.Status == ContentStatus.Published && i.PublishedAt != null && (channel == null || i.Channel.ToLower() == channel.ToLower()))
            .Select(i => new PublishedPostTags(i.Id, i.Channel, i.Format, i.HookType, i.PublishedAt!.Value)).ToListAsync(ct);

    public async Task<int> CountOnDayAsync(Guid productId, string channel, DateOnly day, CancellationToken ct = default)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); var to = from.AddDays(1); var ch = channel.ToLower();
        return await db.Items.AsNoTracking().CountAsync(i => i.ProductId == productId && i.Channel.ToLower() == ch
            && ((i.Status == ContentStatus.Scheduled && i.ScheduledFor >= from && i.ScheduledFor < to) || ((i.Status == ContentStatus.Publishing || i.Status == ContentStatus.Published) && i.PublishedAt >= from && i.PublishedAt < to)), ct);
    }

    public async Task<IReadOnlyList<string>> GetRecentBodiesAsync(Guid productId, string channel, DateTimeOffset since, CancellationToken ct = default)
    {
        var ch = channel.ToLower();
        var ids = await db.Items.AsNoTracking().Where(i => i.ProductId == productId && i.Channel.ToLower() == ch && (i.Status == ContentStatus.Scheduled || i.Status == ContentStatus.Publishing || i.Status == ContentStatus.Published || i.Status == ContentStatus.Approved)
            && (i.PublishedAt >= since || i.ScheduledFor >= since || i.UpdatedAt >= since)).Select(i => new { i.Id, i.CurrentVersion }).ToListAsync(ct);
        var itemIds = ids.Select(x => x.Id).ToList();
        var versions = await db.Versions.AsNoTracking().Where(v => itemIds.Contains(v.ContentItemId)).Select(v => new { v.ContentItemId, v.Number, v.Body }).ToListAsync(ct);
        return versions.Where(v => ids.Any(i => i.Id == v.ContentItemId && i.CurrentVersion == v.Number)).Select(v => v.Body).ToList();
    }
}
