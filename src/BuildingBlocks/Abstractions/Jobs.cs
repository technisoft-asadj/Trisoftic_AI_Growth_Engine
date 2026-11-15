namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public sealed record JobRequest(
    string Type,
    string PayloadJson,
    string? IdempotencyKey = null,
    DateTimeOffset? RunAt = null,
    Guid? ProductId = null,
    int MaxAttempts = 5);

public interface IJobQueue
{
    /// <summary>
    /// Enqueues a job. If IdempotencyKey is set and a job with that key exists, the existing job id is returned (no duplicate effect).
    /// Note: enqueue is its own commit; callers that need atomicity with business data must make the handler idempotent.
    /// </summary>
    Task<Guid> EnqueueAsync(JobRequest request, CancellationToken ct = default);
}

public sealed record JobContext(Guid JobId, string Type, string PayloadJson, int Attempt, int MaxAttempts, string CorrelationId, Guid? ProductId);

/// <summary>Retry later (network, rate limit, provider 5xx).</summary>
public sealed class TransientJobException(string message, TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Do not retry: goes straight to the dead-letter state for human review.</summary>
public sealed class PermanentJobException(string message, Exception? inner = null) : Exception(message, inner);

public interface IJobHandler
{
    /// <summary>Unique job type this handler processes, e.g. "ai.generate-content".</summary>
    string Type { get; }

    /// <summary>When set, the runner will not execute this job while the scope is paused (emergency stop).</summary>
    PauseScope? PauseScope { get; }

    Task HandleAsync(JobContext context, CancellationToken ct);
}
