using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Jobs;

public enum JobStatus { Queued, Running, Succeeded, DeadLettered, Cancelled }

public sealed class Job : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset RunAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CorrelationId { get; set; }
    public Guid? ProductId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public static TimeSpan Backoff(int attempt, Random? rng = null)
    {
        var seconds = Math.Min(10 * Math.Pow(2, Math.Max(0, attempt - 1)), 3600);
        var jitter = (rng ?? Random.Shared).NextDouble() * 0.2 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
