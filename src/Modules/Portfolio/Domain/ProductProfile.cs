using System.Text.Json;
using System.Text.Json.Serialization;

namespace TriSoftic.GrowthEngine.Modules.Portfolio.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClaimStatus { Approved, Prohibited }

/// <summary>A single verifiable statement about the product. AI output must reference these by Id (or be labelled an assumption).</summary>
public sealed class ProfileFact
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "Feature";
    public string Text { get; set; } = "";
    public string? Evidence { get; set; }
    public bool Verified { get; set; }
}

public sealed class ProfileClaim
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public ClaimStatus Status { get; set; } = ClaimStatus.Approved;
    public string? Evidence { get; set; }
}

public sealed class ProfilePersona
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> PainPoints { get; set; } = [];
}

public sealed class ProfileMarket
{
    public string Country { get; set; } = "";
    public List<string> Languages { get; set; } = [];
    public string? Notes { get; set; }
}

public sealed class BrandVoice
{
    public string? Tone { get; set; }
    public List<string> Dos { get; set; } = [];
    public List<string> Donts { get; set; } = [];
}

public sealed class ProfileLink { public string Label { get; set; } = ""; public string Url { get; set; } = ""; }

public sealed class ProfileCompetitor { public string Name { get; set; } = ""; public string? Url { get; set; } public string? Notes { get; set; } }

/// <summary>The structured, versioned product profile (PRD 5.2). Stored as jsonb on ProductProfileVersion.</summary>
public sealed class ProductProfile
{
    public string Summary { get; set; } = "";
    public List<ProfileFact> Facts { get; set; } = [];
    public List<ProfileClaim> Claims { get; set; } = [];
    public List<ProfilePersona> Personas { get; set; } = [];
    public List<ProfileMarket> Markets { get; set; } = [];
    public List<string> Platforms { get; set; } = [];
    public BrandVoice BrandVoice { get; set; } = new();
    public List<ProfileLink> Links { get; set; } = [];
    public List<ProfileCompetitor> Competitors { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static ProductProfile FromJson(string json) => JsonSerializer.Deserialize<ProductProfile>(json, Json) ?? new();
}

public static class ProductProfileValidator
{
    public static readonly string[] FactCategories = ["Feature", "Benefit", "Differentiator", "Pricing", "Integration", "Platform", "Audience", "Other"];

    /// <summary>Normalizes ids (assigns missing ones) then validates. forSubmission adds completeness rules required before approval.</summary>
    public static List<string> Validate(ProductProfile p, bool forSubmission)
    {
        var errors = new List<string>();
        p.Summary = (p.Summary ?? "").Trim();
        if (p.Summary.Length > 4000) errors.Add("Summary is too long (max 4000)");
        if (p.Facts.Count > 500 || p.Claims.Count > 500 || p.Personas.Count > 50 || p.Markets.Count > 100) errors.Add("Too many entries");

        foreach (var f in p.Facts) f.Id = string.IsNullOrWhiteSpace(f.Id) ? "f-" + Guid.NewGuid().ToString("N")[..8] : f.Id.Trim();
        foreach (var c in p.Claims) c.Id = string.IsNullOrWhiteSpace(c.Id) ? "c-" + Guid.NewGuid().ToString("N")[..8] : c.Id.Trim();
        foreach (var x in p.Personas) x.Id = string.IsNullOrWhiteSpace(x.Id) ? "p-" + Guid.NewGuid().ToString("N")[..8] : x.Id.Trim();

        var ids = p.Facts.Select(f => f.Id).Concat(p.Claims.Select(c => c.Id)).Concat(p.Personas.Select(x => x.Id)).ToList();
        if (ids.Count != ids.Distinct().Count()) errors.Add("Fact/claim/persona ids must be unique");

        foreach (var f in p.Facts)
        {
            if (string.IsNullOrWhiteSpace(f.Text)) errors.Add($"Fact {f.Id} has no text");
            if (f.Text?.Length > 1000) errors.Add($"Fact {f.Id} text too long");
            if (!FactCategories.Contains(f.Category)) errors.Add($"Fact {f.Id} has unknown category '{f.Category}'");
        }
        foreach (var c in p.Claims) if (string.IsNullOrWhiteSpace(c.Text)) errors.Add($"Claim {c.Id} has no text");
        foreach (var l in p.Links.Concat(p.Competitors.Where(c => c.Url is not null).Select(c => new ProfileLink { Label = c.Name, Url = c.Url! })))
            if (!Uri.TryCreate(l.Url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                errors.Add($"'{l.Label}' has an invalid URL");

        if (forSubmission)
        {
            if (p.Summary.Length == 0) errors.Add("Summary is required before submitting for approval");
            if (!p.Facts.Any(f => f.Verified)) errors.Add("At least one verified fact is required before submitting for approval");
            foreach (var f in p.Facts.Where(f => f.Verified && string.IsNullOrWhiteSpace(f.Evidence)))
                errors.Add($"Verified fact {f.Id} needs an evidence/source note");
            foreach (var c in p.Claims.Where(c => c.Status == ClaimStatus.Approved && string.IsNullOrWhiteSpace(c.Evidence)))
                errors.Add($"Approved claim {c.Id} needs an evidence/source note");
        }
        return errors;
    }
}
