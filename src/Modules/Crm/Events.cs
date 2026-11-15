using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Crm;

/// <summary>What other modules (workflows) may know about a lead, read fresh each time they ask.</summary>
public sealed class LeadStateProvider(CrmDbContext db) : ISubjectStateProvider
{
    public string SubjectType => "lead";

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(Guid id, CancellationToken ct)
    {
        var l = await db.Leads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return null;
        var name = await db.Contacts.AsNoTracking().Where(c => c.Id == l.ContactId).Select(c => c.DisplayName).FirstOrDefaultAsync(ct);
        return new Dictionary<string, object?>
        {
            ["status"] = l.Status.ToString(), ["source"] = l.Source, ["score"] = l.Score, ["ownerUserId"] = l.OwnerUserId, ["campaignId"] = l.CampaignId,
            ["contactName"] = name, ["nextAction"] = l.NextAction,
        };
    }
}

public sealed class OpportunityStateProvider(CrmDbContext db) : ISubjectStateProvider
{
    public string SubjectType => "opportunity";

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(Guid id, CancellationToken ct)
    {
        var o = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        var stage = await db.Stages.AsNoTracking().Where(s => s.Id == o.StageId).Select(s => new { s.Name, s.Kind }).FirstAsync(ct);
        var name = await db.Contacts.AsNoTracking().Where(c => c.Id == o.ContactId).Select(c => c.DisplayName).FirstOrDefaultAsync(ct);
        return new Dictionary<string, object?>
        {
            ["name"] = o.Name, ["status"] = o.Status.ToString(), ["stage"] = stage.Name, ["stageKind"] = stage.Kind.ToString(), ["value"] = o.Value, ["currency"] = o.Currency,
            ["ownerUserId"] = o.OwnerUserId, ["contactName"] = name,
        };
    }
}

/// <summary>Publishes CRM events after the business change has committed. Publishing never fails the request.</summary>
public static class CrmEvents
{
    public static Task LeadAsync(this IEventPublisher p, string type, Lead l, CancellationToken ct, string? previousStatus = null)
        => p.PublishAsync(new PlatformEvent(type, l.ProductId, "lead", l.Id, previousStatus is null ? null : new Dictionary<string, string?> { ["previousStatus"] = previousStatus }), ct);

    public static Task OpportunityAsync(this IEventPublisher p, string type, Opportunity o, CancellationToken ct, string? previousStage = null)
        => p.PublishAsync(new PlatformEvent(type, o.ProductId, "opportunity", o.Id, previousStage is null ? null : new Dictionary<string, string?> { ["previousStage"] = previousStage }), ct);
}
