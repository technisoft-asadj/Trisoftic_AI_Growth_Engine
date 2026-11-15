using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Jobs;

public static class JobsModule
{
    /// <param name="runWorker">true in the Worker host (and in-process for dev/tests via Jobs:RunInProcess).</param>
    public static IServiceCollection AddJobsModule(this IServiceCollection s, IConfiguration c, bool runWorker)
    {
        s.AddModuleDbContext<JobsDbContext>(c, JobsDbContext.Schema);
        s.AddScoped<IJobQueue, JobQueue>();
        s.Configure<JobRunnerOptions>(c.GetSection("Jobs"));
        s.AddSingleton<JobRunner>();
        if (runWorker || c.GetValue<bool>("Jobs:RunInProcess")) s.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
        return s;
    }

    public sealed record JobDto(Guid Id, string Type, JobStatus Status, int Attempts, int MaxAttempts, DateTimeOffset RunAt, string? LastError, Guid? ProductId, string? CorrelationId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/jobs").RequireAuthorization().WithTags("Jobs");

        g.MapGet("/", async (JobsDbContext db, ICurrentUser user, JobStatus? status, string? type, int? take, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.JobsManage)) return Results.Forbid();
            var q = db.Jobs.AsNoTracking().AsQueryable();
            if (status is not null) q = q.Where(j => j.Status == status);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(j => j.Type == type);
            var rows = await q.OrderByDescending(j => j.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto));
        });

        g.MapGet("/summary", async (JobsDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.JobsManage)) return Results.Forbid();
            var counts = await db.Jobs.AsNoTracking().GroupBy(j => j.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToListAsync(ct);
            return Results.Ok(counts.ToDictionary(x => x.Status.ToString(), x => x.Count));
        });

        g.MapPost("/{id:guid}/retry", async (Guid id, JobsDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.JobsManage)) return Results.Forbid();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return Results.NotFound();
            if (job.Status is not (JobStatus.DeadLettered or JobStatus.Cancelled)) return Results.Problem(statusCode: 409, title: $"Cannot retry a job in state {job.Status}");
            job.Status = JobStatus.Queued; job.Attempts = 0; job.RunAt = clock.UtcNow; job.CompletedAt = null; job.LastError = null;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("job.replayed", "Job", id.ToString(), job.ProductId, job.Type), ct);
            return Results.Ok(ToDto(job));
        });

        g.MapPost("/{id:guid}/cancel", async (Guid id, JobsDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.JobsManage)) return Results.Forbid();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return Results.NotFound();
            if (job.Status is not (JobStatus.Queued or JobStatus.DeadLettered)) return Results.Problem(statusCode: 409, title: $"Cannot cancel a job in state {job.Status}");
            job.Status = JobStatus.Cancelled; job.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("job.cancelled", "Job", id.ToString(), job.ProductId, job.Type), ct);
            return Results.Ok(ToDto(job));
        });

        return app;
    }

    private static JobDto ToDto(Job j) => new(j.Id, j.Type, j.Status, j.Attempts, j.MaxAttempts, j.RunAt, j.LastError, j.ProductId, j.CorrelationId, j.CreatedAt, j.CompletedAt);
}
