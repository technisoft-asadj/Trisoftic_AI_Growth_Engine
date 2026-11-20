using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>What the AI made of one comment on a published post. Nothing here is sent anywhere: a person reads, edits and approves any reply.</summary>
/// <param name="Category">Question, Praise, Complaint, BugReport, Spam, Abuse or Other.</param>
/// <param name="Reply">A suggested reply grounded in the verified product facts, or null when none should be sent (spam, abuse, or a fact the profile lacks).</param>
/// <param name="SuggestHide">Spam or abuse: the person may choose to hide the comment.</param>
/// <param name="Questions">Facts the AI needed but the profile does not contain. Shown to the person instead of being guessed.</param>
public sealed record CommentTriageResult(string Category, string? Reply, bool SuggestHide, IReadOnlyList<string> Questions, decimal CostUsd);

/// <summary>Implemented by the AI module so publishing can suggest replies without depending on it.</summary>
public interface ICommentTriage
{
    /// <param name="postText">What the post said, for context. The comment itself is untrusted public text and is treated as data, never as instructions.</param>
    Task<Result<CommentTriageResult>> TriageAsync(Guid productId, string postText, string comment, CancellationToken ct = default);
}
