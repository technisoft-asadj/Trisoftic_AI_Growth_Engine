using System.Text;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public enum LeadStatus { New, Contacted, Qualified, Disqualified, Converted }
public enum ScoreField { SourceEquals, HasCompany, HasPhone, JobTitleContains, TagEquals, MessageContains }

public sealed class Lead : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ContactId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? FormId { get; set; }
    public string Source { get; set; } = "manual";
    public string? Message { get; set; }
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public Guid? OwnerUserId { get; set; }
    public int Score { get; set; }
    public string? NextAction { get; set; }
    public DateTimeOffset? NextActionDue { get; set; }
    public string? QualificationNote { get; set; }
    public string? DisqualifyReason { get; set; }
    public Guid? ConvertedOpportunityId { get; set; }
    /// <summary>Null until a human actually made contact: response-time metrics must not treat "not yet" as zero.</summary>
    public DateTimeOffset? FirstContactedAt { get; set; }
    public DateTimeOffset? QualifiedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsOpen => Status is LeadStatus.New or LeadStatus.Contacted or LeadStatus.Qualified;

    public void MarkContacted(DateTimeOffset at)
    {
        if (Status != LeadStatus.New) throw new IllegalTransitionException($"Only a New lead can be marked contacted (was {Status})");
        Status = LeadStatus.Contacted; FirstContactedAt = at;
    }

    public void Qualify(string note, DateTimeOffset at)
    {
        if (Status != LeadStatus.Contacted) throw new IllegalTransitionException($"Only a Contacted lead can be qualified (was {Status})");
        if (string.IsNullOrWhiteSpace(note)) throw new IllegalTransitionException("A qualification note is required");
        Status = LeadStatus.Qualified; QualificationNote = note.Trim(); QualifiedAt = at;
    }

    public void Disqualify(string reason, DateTimeOffset at)
    {
        if (!IsOpen) throw new IllegalTransitionException($"A {Status} lead cannot be disqualified");
        if (string.IsNullOrWhiteSpace(reason)) throw new IllegalTransitionException("A disqualification reason is required");
        Status = LeadStatus.Disqualified; DisqualifyReason = reason.Trim(); ClosedAt = at;
    }

    public void Reopen()
    {
        if (Status != LeadStatus.Disqualified) throw new IllegalTransitionException($"Only a Disqualified lead can be reopened (was {Status})");
        Status = LeadStatus.New; DisqualifyReason = null; ClosedAt = null; FirstContactedAt = null; QualifiedAt = null;
    }

    public void Convert(Guid opportunityId, DateTimeOffset at)
    {
        if (Status != LeadStatus.Qualified) throw new IllegalTransitionException("Only a Qualified lead can be converted to an opportunity");
        Status = LeadStatus.Converted; ConvertedOpportunityId = opportunityId; ClosedAt = at;
    }
}

public sealed class LeadForm : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? CampaignId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Unguessable public token used in the form's submit URL.</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>Exact words shown next to the consent checkbox. Null means the form does not ask for marketing consent.</summary>
    public string? ConsentText { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Comma-separated user ids that receive new leads in round-robin order.</summary>
    public string OwnerPool { get; set; } = "";
    public int RoundRobinCursor { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ScoreRule : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? ProductId { get; set; }
    public string Name { get; set; } = "";
    public ScoreField Field { get; set; }
    public string? Value { get; set; }
    public int Points { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record ScoreInput(string Source, bool HasCompany, bool HasPhone, string? JobTitle, IReadOnlyCollection<string> Tags, string? Message);
public sealed record ScoreHit(string Rule, int Points);

/// <summary>Explainable, deterministic scoring: the sum of matching rule points, clamped to 0-100.</summary>
public static class Scorer
{
    public static (int Score, List<ScoreHit> Hits) Evaluate(IEnumerable<ScoreRule> rules, Guid productId, ScoreInput i)
    {
        var hits = new List<ScoreHit>();
        foreach (var r in rules.Where(r => r.Enabled && (r.ProductId is null || r.ProductId == productId)))
        {
            var v = r.Value?.Trim();
            var match = r.Field switch
            {
                ScoreField.SourceEquals => v is not null && string.Equals(i.Source, v, StringComparison.OrdinalIgnoreCase),
                ScoreField.HasCompany => i.HasCompany,
                ScoreField.HasPhone => i.HasPhone,
                ScoreField.JobTitleContains => v is { Length: > 0 } && (i.JobTitle?.Contains(v, StringComparison.OrdinalIgnoreCase) ?? false),
                ScoreField.TagEquals => v is not null && i.Tags.Contains(v.ToLowerInvariant()),
                ScoreField.MessageContains => v is { Length: > 0 } && (i.Message?.Contains(v, StringComparison.OrdinalIgnoreCase) ?? false),
                _ => false,
            };
            if (match) hits.Add(new ScoreHit(r.Name, r.Points));
        }
        return (Math.Clamp(hits.Sum(h => h.Points), 0, 100), hits);
    }
}
