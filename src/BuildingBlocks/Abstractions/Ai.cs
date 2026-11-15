namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>Quality/cost tier. Models per tier are configuration, never code (ADR-11).</summary>
public enum AiTier { Draft, Strategy }

public sealed record AiRequest(
    string Purpose,            // e.g. "strategy.brief", "content.draft"
    AiTier Tier,
    string System,
    string User,
    Guid? ProductId = null,
    string? TemplateKey = null,
    int TemplateVersion = 1,
    int MaxTokens = 8000,
    string? ProviderOverride = null);

public enum AiOutcome { Succeeded, Failed, Refused, BudgetBlocked, Paused }

public sealed record AiResult(
    string Text,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCostUsd);

public class AiException(string message, AiOutcome outcome, Exception? inner = null) : Exception(message, inner)
{
    public AiOutcome Outcome { get; } = outcome;
}

/// <summary>Provider-neutral text generation. Records usage, enforces budgets/pause, redacts sensitive data.</summary>
public interface IAiGenerator
{
    Task<AiResult> GenerateAsync(AiRequest request, CancellationToken ct = default);
}

// ---------- verified product knowledge (implemented by Portfolio) ----------

public sealed record KnowledgeFact(string Id, string Category, string Text);
public sealed record KnowledgeClaim(string Id, string Text, bool Prohibited);
public sealed record KnowledgePersona(string Id, string Name, string? Description, IReadOnlyList<string> PainPoints);
public sealed record KnowledgeMarket(string Country, IReadOnlyList<string> Languages);

public sealed record ApprovedKnowledge(
    Guid ProductId,
    string ProductName,
    string Kind,
    string Lifecycle,
    int ProfileVersion,
    string Summary,
    IReadOnlyList<KnowledgeFact> Facts,
    IReadOnlyList<KnowledgeClaim> Claims,
    IReadOnlyList<KnowledgePersona> Personas,
    IReadOnlyList<KnowledgeMarket> Markets,
    IReadOnlyList<string> Platforms,
    string? Tone,
    IReadOnlyList<string> Dos,
    IReadOnlyList<string> Donts);

/// <summary>Only APPROVED, VERIFIED profile data is exposed to AI. Returns null if the product has no approved profile.</summary>
public interface IProductKnowledge
{
    Task<ApprovedKnowledge?> GetApprovedAsync(Guid productId, CancellationToken ct = default);
}
