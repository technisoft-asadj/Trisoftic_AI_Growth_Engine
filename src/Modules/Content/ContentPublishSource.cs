using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Content;

/// <summary>The content module's side of the publishing contract. All status changes go through the item's state machine.</summary>
public sealed class ContentPublishSource(ContentDbContext db, IClock clock) : IContentPublishSource
{
    public async Task<IReadOnlyList<DueContent>> GetDueAsync(DateTimeOffset now, int take, CancellationToken ct)
        => await db.Items.AsNoTracking().Where(i => i.Status == ContentStatus.Scheduled && i.ScheduledFor != null && i.ScheduledFor <= now).OrderBy(i => i.ScheduledFor).Take(take)
            .Select(i => new DueContent(i.Id, i.ProductId, i.Channel, i.CurrentVersion, i.ScheduledFor!.Value)).ToListAsync(ct);

    public async Task<PublishableContent?> GetAsync(Guid itemId, int version, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return null;
        var v = await db.Versions.AsNoTracking().FirstOrDefaultAsync(x => x.ContentItemId == itemId && x.Number == version, ct);
        return v is null ? null : new PublishableContent(item.Id, item.ProductId, item.Channel, item.Format, v.Title, v.Body, version, item.CurrentVersion, item.ApprovedVersion, item.Status.ToString(), v.AssetIds);
    }

    private async Task<StateChange> ChangeAsync(Guid itemId, Action<ContentItem> change, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return new StateChange(false, "Content not found");
        try { change(item); } catch (IllegalTransitionException ex) { return new StateChange(false, ex.Message); }
        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new StateChange(true);
    }

    public Task<StateChange> BeginPublishAsync(Guid itemId, int version, CancellationToken ct) => ChangeAsync(itemId, i => i.BeginPublish(version), ct);
    public Task<StateChange> CompletePublishAsync(Guid itemId, int version, DateTimeOffset at, CancellationToken ct) => ChangeAsync(itemId, i => i.CompletePublish(version, at), ct);
    public Task<StateChange> FailPublishAsync(Guid itemId, int version, string reason, CancellationToken ct) => ChangeAsync(itemId, i => i.FailPublish(version, reason), ct);
    public Task<StateChange> RequeueAsync(Guid itemId, int version, DateTimeOffset at, CancellationToken ct) => ChangeAsync(itemId, i => i.Requeue(version, at), ct);
    public Task<StateChange> AbandonAsync(Guid itemId, int version, CancellationToken ct) => ChangeAsync(itemId, i => i.Abandon(version), ct);

    public async Task<IReadOnlyList<FailedContent>> GetFailedAsync(IReadOnlyCollection<Guid>? productScope, CancellationToken ct)
    {
        var q = db.Items.AsNoTracking().Where(i => i.Status == ContentStatus.PublishFailed);
        if (productScope is not null) q = q.Where(i => productScope.Contains(i.ProductId));
        var items = await q.OrderByDescending(i => i.UpdatedAt).Take(200).ToListAsync(ct);
        return items.Select(i => new FailedContent(i.Id, i.ProductId, i.Channel, i.Title, i.CurrentVersion, i.LastPublishError, i.UpdatedAt)).ToList();
    }
}
