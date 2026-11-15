using System.Text.Json;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Governance.Audit;

/// <summary>
/// Writes immediately in its own commit. Callers invoke it AFTER their business commit succeeds; a failure here
/// propagates (the request errors and is logged) rather than being swallowed.
/// </summary>
public sealed class AuditWriter(GovernanceDbContext db, ICurrentUser user, ICorrelationContext corr, IClock clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(AuditEntry e, CancellationToken ct = default)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = clock.UtcNow,
            ActorUserId = user.UserId,
            ActorEmail = user.Email,
            Action = e.Action,
            SubjectType = e.SubjectType,
            SubjectId = e.SubjectId,
            ProductId = e.ProductId,
            Summary = e.Summary,
            BeforeJson = e.Before is null ? null : JsonSerializer.Serialize(e.Before, Json),
            AfterJson = e.After is null ? null : JsonSerializer.Serialize(e.After, Json),
            CorrelationId = corr.CorrelationId,
        });
        await db.SaveChangesAsync(ct);
    }
}
