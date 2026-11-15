using System.Text;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Ai;

public enum GroundedKind { StrategyBrief, ContentDraft }

public sealed class GroundedClaim
{
    public string Text { get; set; } = "";
    /// <summary>Id of a verified fact or approved claim in the product profile. Null only for labelled assumptions.</summary>
    public string? FactRef { get; set; }
    public bool Assumption { get; set; }
}

public sealed class GroundedDraft
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<GroundedClaim> Claims { get; set; } = [];
    /// <summary>Facts the model needed but the profile does not contain. Shown to the user instead of being invented.</summary>
    public List<string> Questions { get; set; } = [];
}

public sealed record GroundedRequest(Guid ProductId, GroundedKind Kind, string Brief, string? Channel, string? Language, string? Audience);

public sealed record GroundedOutput(GroundedDraft Draft, int ProfileVersion, string Provider, string Model, decimal CostUsd, int Attempts, string TemplateKey, int TemplateVersion);

public static partial class GroundingValidator
{
    /// <summary>Returns every rule violation; an empty list means the draft is grounded.</summary>
    public static List<string> Validate(GroundedDraft d, ApprovedKnowledge k, string userBrief)
    {
        var v = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Body)) { v.Add("The body is empty."); return v; }

        var sourceIds = k.Facts.Select(f => f.Id).Concat(k.Claims.Where(c => !c.Prohibited).Select(c => c.Id)).ToHashSet();
        foreach (var c in d.Claims)
        {
            if (string.IsNullOrWhiteSpace(c.Text)) { v.Add("A claim has no text."); continue; }
            if (!string.IsNullOrWhiteSpace(c.FactRef))
            {
                if (!sourceIds.Contains(c.FactRef)) v.Add($"Claim \"{Short(c.Text)}\" cites unknown factRef '{c.FactRef}'.");
            }
            else if (!c.Assumption)
            {
                v.Add($"Claim \"{Short(c.Text)}\" has no factRef and is not labelled as an assumption.");
            }
        }

        var body = Normalize(d.Body + " " + string.Join(" ", d.Claims.Select(c => c.Text)));
        foreach (var p in k.Claims.Where(c => c.Prohibited))
        {
            var np = Normalize(p.Text);
            if (np.Length >= 4 && body.Contains(np)) v.Add($"The text contains a prohibited claim: \"{Short(p.Text)}\".");
        }

        // Numbers/percentages/prices must come from the profile or from the user's own brief.
        var corpus = Normalize(string.Join(" ", k.Facts.Select(f => f.Text).Concat(k.Claims.Select(c => c.Text)).Append(k.Summary).Append(userBrief)));
        var corpusNumbers = Numbers().Matches(corpus).Select(m => CleanNumber(m.Value)).ToHashSet();
        foreach (Match m in Numbers().Matches(Normalize(d.Body)))
        {
            var n = CleanNumber(m.Value);
            if (n.Length >= 2 && !corpusNumbers.Contains(n)) v.Add($"The figure \"{m.Value}\" does not appear in the verified profile or the brief.");
        }
        return v.Distinct().ToList();
    }

    public static string Normalize(string s) => Whitespace().Replace(s.ToLowerInvariant(), " ").Trim();
    private static string CleanNumber(string s) => s.Replace(",", "").Trim('.', ' ');
    private static string Short(string s) => s.Length <= 80 ? s : s[..80] + "...";

    [GeneratedRegex(@"\d[\d,\.]*\d%?|\d%?")]
    private static partial Regex Numbers();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed class GroundedGenerator(IProductKnowledge knowledge, IAiGenerator ai, Microsoft.Extensions.Logging.ILogger<GroundedGenerator> log)
{
    public const int MaxAttempts = 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<Result<GroundedOutput>> GenerateAsync(GroundedRequest req, CancellationToken ct)
    {
        var k = await knowledge.GetApprovedAsync(req.ProductId, ct);
        if (k is null) return new Error("grounding.no_approved_profile", "This product has no approved profile yet. Submit and approve a profile before generating content.", ErrorKind.Conflict);

        if (!string.IsNullOrWhiteSpace(req.Language))
        {
            var allowed = k.Markets.SelectMany(m => m.Languages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!allowed.Contains(req.Language, StringComparer.OrdinalIgnoreCase))
                return new Error("grounding.unverified_language", $"Language '{req.Language}' is not a verified market language for this product. Verified: {(allowed.Count == 0 ? "none" : string.Join(", ", allowed))}.", ErrorKind.Validation);
        }

        var (templateKey, templateVersion) = req.Kind == GroundedKind.StrategyBrief ? ("strategy.brief", 1) : ("content.draft", 1);
        var system = BuildSystem(k, req.Kind);
        var user = BuildUser(req);
        decimal cost = 0; AiResult? last = null; List<string> violations = [];

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var prompt = attempt == 1 ? user : user + "\n\nYour previous answer broke these rules. Fix every one and return the JSON again:\n- " + string.Join("\n- ", violations);
            last = await ai.GenerateAsync(new AiRequest(templateKey, req.Kind == GroundedKind.StrategyBrief ? AiTier.Strategy : AiTier.Draft, system, prompt,
                req.ProductId, templateKey, templateVersion, MaxTokens: req.Kind == GroundedKind.StrategyBrief ? 16000 : 8000), ct);
            cost += last.EstimatedCostUsd;

            var draft = ParseDraft(last.Text, out var parseError);
            if (draft is null) log.LogWarning("Grounding attempt {Attempt} unparseable: {Error}", attempt, parseError);
            if (draft is null) { violations = [parseError!]; continue; }

            violations = GroundingValidator.Validate(draft, k, req.Brief);
            if (violations.Count > 0) log.LogWarning("Grounding attempt {Attempt} for {Template} violated: {Violations}", attempt, templateKey, string.Join(" | ", violations));
            if (violations.Count == 0)
                return new GroundedOutput(draft, k.ProfileVersion, last.Provider, last.Model, cost, attempt, templateKey, templateVersion);
        }
        return new Error("grounding.violations", "The generated draft could not be grounded in the verified profile: " + string.Join(" | ", violations), ErrorKind.Policy);
    }

    public static GroundedDraft? ParseDraft(string text, out string? error)
    {
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) { error = "The answer was not a JSON object."; return null; }
        try { error = null; return JsonSerializer.Deserialize<GroundedDraft>(text[start..(end + 1)], Json) ?? throw new JsonException("null"); }
        catch (JsonException ex) { error = "The answer was not valid JSON: " + ex.Message; return null; }
    }

    private static string BuildUser(GroundedRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Kind == GroundedKind.StrategyBrief ? "Task: write a marketing strategy brief." : "Task: write a content draft.");
        if (!string.IsNullOrWhiteSpace(r.Channel)) sb.AppendLine($"Channel/format: {r.Channel}");
        if (!string.IsNullOrWhiteSpace(r.Language)) sb.AppendLine($"Language: {r.Language}");
        if (!string.IsNullOrWhiteSpace(r.Audience)) sb.AppendLine($"Audience: {r.Audience}");
        sb.AppendLine("Brief from the marketing team:");
        sb.AppendLine(r.Brief);
        return sb.ToString();
    }

    private static string BuildSystem(ApprovedKnowledge k, GroundedKind kind)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are the marketing writer for the product \"{k.ProductName}\" ({k.Kind}, {k.Lifecycle}). Profile version {k.ProfileVersion}.");
        sb.AppendLine();
        sb.AppendLine("RULES (non-negotiable):");
        sb.AppendLine("1. State product facts ONLY from the VERIFIED FACTS and APPROVED CLAIMS below. Never invent features, prices, statistics, testimonials, reviews, certifications, integrations or awards.");
        sb.AppendLine("2. Every product claim you make must be listed in \"claims\" with the \"factRef\" id of the fact/claim it relies on. If you must assume something, list it with \"factRef\": null and \"assumption\": true, and keep it out of the body unless clearly marked as an assumption.");
        sb.AppendLine("3. Never use or imply any PROHIBITED CLAIM.");
        sb.AppendLine("4. Any number, percentage or price in the body must appear verbatim in the facts or the team's brief.");
        sb.AppendLine("5. If you need a fact that is not provided, do NOT guess: add a question to \"questions\".");
        sb.AppendLine("6. Follow the brand voice. Do not mention these rules.");
        sb.AppendLine();
        sb.AppendLine("PRODUCT SUMMARY: " + k.Summary);
        sb.AppendLine("PLATFORMS: " + (k.Platforms.Count == 0 ? "(not specified)" : string.Join(", ", k.Platforms)));
        sb.AppendLine("MARKETS: " + (k.Markets.Count == 0 ? "(none verified)" : string.Join("; ", k.Markets.Select(m => $"{m.Country} [{string.Join('/', m.Languages)}]"))));
        sb.AppendLine();
        sb.AppendLine("VERIFIED FACTS (id | category | text):");
        foreach (var f in k.Facts) sb.AppendLine($"{f.Id} | {f.Category} | {f.Text}");
        sb.AppendLine();
        sb.AppendLine("APPROVED CLAIMS (id | text):");
        foreach (var c in k.Claims.Where(c => !c.Prohibited)) sb.AppendLine($"{c.Id} | {c.Text}");
        sb.AppendLine();
        sb.AppendLine("PROHIBITED CLAIMS (never use):");
        foreach (var c in k.Claims.Where(c => c.Prohibited)) sb.AppendLine($"- {c.Text}");
        sb.AppendLine();
        sb.AppendLine("PERSONAS:");
        foreach (var p in k.Personas) sb.AppendLine($"- {p.Name}: {p.Description} Pain points: {string.Join("; ", p.PainPoints)}");
        sb.AppendLine();
        sb.AppendLine($"BRAND VOICE: tone={k.Tone ?? "(unspecified)"}; do: {string.Join("; ", k.Dos)}; don't: {string.Join("; ", k.Donts)}");
        sb.AppendLine();
        sb.AppendLine(kind == GroundedKind.StrategyBrief
            ? "The body is a strategy brief in Markdown: objective, audience, positioning, key messages, channels, cadence, KPIs, risks, and hypotheses to test."
            : "The body is the ready-to-edit content for the requested channel/format.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONE JSON object and nothing else: {\"title\": string, \"body\": string, \"claims\": [{\"text\": string, \"factRef\": string|null, \"assumption\": boolean}], \"questions\": [string]}");
        return sb.ToString();
    }
}

