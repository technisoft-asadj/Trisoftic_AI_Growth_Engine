using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Content;

public sealed class ContentCatalog(ContentDbContext db, IAssetCatalog assets) : IContentCatalog
{
    public async Task<IReadOnlyList<PublishedPostTags>> GetPublishedAsync(Guid productId, string? channel, CancellationToken ct = default)
    {
        var items = await db.Items.AsNoTracking().Where(i => i.ProductId == productId && i.Status == ContentStatus.Published && i.PublishedAt != null && (channel == null || i.Channel.ToLower() == channel.ToLower()))
            .Select(i => new { i.Id, i.Channel, i.Format, i.HookType, PublishedAt = i.PublishedAt!.Value, i.CurrentVersion }).ToListAsync(ct);
        if (items.Count == 0) return [];
        var ids = items.Select(i => i.Id).ToList();
        var versions = (await db.Versions.AsNoTracking().Where(v => ids.Contains(v.ContentItemId)).Select(v => new { v.ContentItemId, v.Number, v.AssetIdsJson }).ToListAsync(ct))
            .ToDictionary(v => (v.ContentItemId, v.Number), v => v.AssetIdsJson);
        var known = (await assets.ListAsync(productId, ct)).ToDictionary(a => a.Id);
        string VisualOf(Guid item, int version)
        {
            if (!versions.TryGetValue((item, version), out var json) || !json.StartsWith('[')) return "Text only";
            var files = (System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(json) ?? []).Select(id => known.GetValueOrDefault(id)).Where(a => a is not null).Select(a => a!).ToList();
            if (files.Count == 0) return "Text only";
            if (files.Any(MediaRules.IsVideo)) return "Video";
            return files.All(a => a.IsSynthetic) ? "AI picture" : "Own picture";
        }
        return [.. items.Select(i => new PublishedPostTags(i.Id, i.Channel, i.Format, i.HookType, i.PublishedAt, VisualOf(i.Id, i.CurrentVersion)))];
    }

    public async Task<IReadOnlySet<Guid>> GetUsedAssetIdsAsync(Guid productId, CancellationToken ct = default)
    {
        var alive = await db.Items.AsNoTracking().Where(i => i.ProductId == productId && i.Status != ContentStatus.Cancelled && i.Status != ContentStatus.Rejected).Select(i => i.Id).ToListAsync(ct);
        var json = await db.Versions.AsNoTracking().Where(v => alive.Contains(v.ContentItemId) && v.AssetIdsJson != "[]").Select(v => v.AssetIdsJson).ToListAsync(ct);
        return json.Where(j => j.StartsWith('[')).SelectMany(j => System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(j) ?? []).ToHashSet();
    }

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
