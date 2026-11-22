using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Content;

/// <summary>
/// The content module's side of the growth-plan contract. It follows exactly the same rules as the content endpoints:
/// every item starts as version 1 in Draft, only the approved current version can be scheduled, and the time must be in the future.
/// Permission to call this is checked by the caller (the plan endpoints), which acts as the signed-in user.
/// </summary>
public sealed class ContentPlanner(ContentDbContext db, ICurrentUser user, IApprovalService approvals, IAssetCatalog assets, IClock clock, IAuditWriter audit) : IContentPlanner
{
    public async Task<Guid> CreateAsync(NewContent c, CancellationToken ct = default)
    {
        var format = ContentModule.Formats.Contains(c.Format) ? c.Format : "Post";
        var now = clock.UtcNow;
        var item = new ContentItem { ProductId = c.ProductId, Title = c.Title.Trim()[..Math.Min(300, c.Title.Trim().Length)], Channel = c.Channel.Trim(), Format = format, HookType = HookTypes.IsValid(c.HookType) ? c.HookType : null, Language = c.Language, CurrentVersion = 1, CreatedBy = user.UserId, CreatedAt = now, UpdatedAt = now };
        db.Items.Add(item);
        db.Versions.Add(new ContentVersion { ContentItemId = item.Id, Number = 1, Title = item.Title, Body = c.Body, Note = c.SourceDraftId is null ? "Created" : "Created from AI draft", SourceDraftId = c.SourceDraftId, AssetIdsJson = ContentVersion.ToJson(c.AssetIds ?? []), CreatedBy = user.UserId, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("content.created", "ContentItem", item.Id.ToString(), c.ProductId, item.Title, After: new { item.Channel, item.Format, c.SourceDraftId }), ct);
        return item.Id;
    }

    public async Task<StateChange> SubmitAsync(Guid itemId, CancellationToken ct = default)
    {
        var i = await db.Items.FirstOrDefaultAsync(x => x.Id == itemId, ct);
        if (i is null) return new StateChange(false, "Content not found");
        var cur = await db.Versions.AsNoTracking().FirstAsync(v => v.ContentItemId == itemId && v.Number == i.CurrentVersion, ct);
        if (string.IsNullOrWhiteSpace(cur.Body) && i.Format != "Story") return new StateChange(false, "Add some content before submitting for approval");
        var found = cur.AssetIds.Count == 0 ? [] : await assets.GetAsync(cur.AssetIds, ct);
        if (MediaRules.Validate(i.Format, found, i.ProductId, clock.UtcNow, complete: true, i.Channel) is { Count: > 0 } mediaErrors) return new StateChange(false, string.Join(" ", mediaErrors));
        try { i.Submit(); } catch (IllegalTransitionException ex) { return new StateChange(false, ex.Message); }
        i.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        var r = await approvals.RequestAsync(new ApprovalRequest(ContentApprovalHandler.Subject, itemId.ToString(), i.CurrentVersion, i.ProductId, $"{i.Title} v{i.CurrentVersion} ({i.Channel})", $"{i.Format} for {i.Channel}"), ct);
        if (!r.IsSuccess) { i.Withdraw(); await db.SaveChangesAsync(ct); return new StateChange(false, r.Error!.Message); }
        return new StateChange(true);
    }

    public async Task<StateChange> ScheduleAsync(Guid itemId, DateTimeOffset at, CancellationToken ct = default)
    {
        var i = await db.Items.FirstOrDefaultAsync(x => x.Id == itemId, ct);
        if (i is null) return new StateChange(false, "Content not found");
        try { i.Schedule(at, clock.UtcNow); } catch (IllegalTransitionException ex) { return new StateChange(false, ex.Message); }
        i.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("content.scheduled", "ContentItem", itemId.ToString(), i.ProductId, $"{i.Title} v{i.CurrentVersion}", After: new { at, i.Channel }), ct);
        return new StateChange(true);
    }

    public async Task<IReadOnlyList<ContentStatusInfo>> GetStatusesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
        => await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).Select(i => new ContentStatusInfo(i.Id, i.Title, i.Status.ToString(), i.CurrentVersion, i.ApprovedVersion, i.ScheduledFor)).ToListAsync(ct);
}
