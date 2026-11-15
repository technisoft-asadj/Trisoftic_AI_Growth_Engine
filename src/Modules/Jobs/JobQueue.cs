using Microsoft.EntityFrameworkCore;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Jobs;

public sealed class JobQueue(JobsDbContext db, IClock clock, ICorrelationContext corr) : IJobQueue
{
    public async Task<Guid> EnqueueAsync(JobRequest r, CancellationToken ct = default)
    {
        if (r.IdempotencyKey is not null)
        {
            var existing = await db.Jobs.AsNoTracking().Where(j => j.IdempotencyKey == r.IdempotencyKey).Select(j => (Guid?)j.Id).FirstOrDefaultAsync(ct);
            if (existing is not null) return existing.Value;
        }

        var now = clock.UtcNow;
        var job = new Job
        {
            Type = r.Type, PayloadJson = r.PayloadJson, IdempotencyKey = r.IdempotencyKey, ProductId = r.ProductId,
            MaxAttempts = r.MaxAttempts, RunAt = r.RunAt ?? now, CreatedAt = now, CorrelationId = corr.CorrelationId,
        };
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
            return job.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && r.IdempotencyKey is not null)
        {
            // Lost a race with a concurrent enqueue of the same key: return the winner.
            db.Entry(job).State = EntityState.Detached;
            return await db.Jobs.AsNoTracking().Where(j => j.IdempotencyKey == r.IdempotencyKey).Select(j => j.Id).FirstAsync(ct);
        }
    }
}
