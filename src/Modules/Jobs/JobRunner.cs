using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Jobs;

public sealed class JobRunnerOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PausedRecheck { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Claims due jobs with FOR UPDATE SKIP LOCKED so multiple workers are safe, executes the matching handler in a DI scope,
/// and applies retry/backoff/dead-letter rules. Jobs whose lease expired (worker died) are re-claimed.
/// </summary>
public sealed class JobRunner(IServiceScopeFactory scopes, IClock clock, Microsoft.Extensions.Options.IOptions<JobRunnerOptions> options, ILogger<JobRunner> log) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            bool didWork;
            try { didWork = await RunOnceAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Job runner loop error"); didWork = false; }
            if (!didWork) await Task.Delay(options.Value.PollInterval, stop).ContinueWith(_ => { });
        }
    }

    /// <summary>Processes at most one job. Returns true if a job was claimed.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<JobsDbContext>();
        var now = clock.UtcNow;
        var opt = options.Value;

        Job? job;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            job = (await db.Jobs.FromSql($"""
                SELECT * FROM jobs.jobs
                WHERE ("Status" = 'Queued' AND "RunAt" <= {now})
                   OR ("Status" = 'Running' AND "LockedUntil" < {now})
                ORDER BY "RunAt" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct)).FirstOrDefault();
            if (job is null) return false;

            job.Status = JobStatus.Running;
            job.Attempts++;
            job.LockedBy = _workerId;
            job.LockedUntil = now + opt.Lease;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var corr = sp.GetRequiredService<ICorrelationContext>();
        corr.CorrelationId = job.CorrelationId ?? job.Id.ToString("N");
        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = corr.CorrelationId, ["JobId"] = job.Id, ["JobType"] = job.Type });

        var handler = sp.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        if (handler is null)
        {
            await FinishAsync(db, job, JobStatus.DeadLettered, $"No handler registered for job type '{job.Type}'");
            return true;
        }

        if (handler.PauseScope is { } scopeToCheck && await sp.GetRequiredService<IPauseSwitch>().IsPausedAsync(scopeToCheck, ct))
        {
            // Emergency stop: put it back without burning an attempt.
            job.Status = JobStatus.Queued; job.Attempts--; job.RunAt = clock.UtcNow + opt.PausedRecheck;
            job.LockedBy = null; job.LockedUntil = null; job.LastError = $"Paused ({scopeToCheck})";
            await db.SaveChangesAsync(ct);
            return true;
        }

        try
        {
            await handler.HandleAsync(new JobContext(job.Id, job.Type, job.PayloadJson, job.Attempts, job.MaxAttempts, corr.CorrelationId, job.ProductId), ct);
            await FinishAsync(db, job, JobStatus.Succeeded, null);
        }
        catch (PermanentJobException ex)
        {
            log.LogError(ex, "Job {JobId} failed permanently", job.Id);
            await FinishAsync(db, job, JobStatus.DeadLettered, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var retryAfter = (ex as TransientJobException)?.RetryAfter;
            if (job.Attempts >= job.MaxAttempts)
            {
                log.LogError(ex, "Job {JobId} exhausted {Attempts} attempts", job.Id, job.Attempts);
                await FinishAsync(db, job, JobStatus.DeadLettered, Truncate($"Exhausted retries: {ex.Message}"));
            }
            else
            {
                log.LogWarning(ex, "Job {JobId} attempt {Attempt} failed; will retry", job.Id, job.Attempts);
                job.Status = JobStatus.Queued;
                job.RunAt = clock.UtcNow + (retryAfter ?? Job.Backoff(job.Attempts));
                job.LockedBy = null; job.LockedUntil = null; job.LastError = Truncate(ex.Message);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        return true;
    }

    private async Task FinishAsync(JobsDbContext db, Job job, JobStatus status, string? error)
    {
        job.Status = status; job.LastError = error is null ? null : Truncate(error);
        job.CompletedAt = clock.UtcNow; job.LockedBy = null; job.LockedUntil = null;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string Truncate(string s) => s.Length <= 3900 ? s : s[..3900];
}
