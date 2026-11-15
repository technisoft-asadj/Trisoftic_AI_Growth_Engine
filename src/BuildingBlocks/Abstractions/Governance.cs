namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public sealed record AuditEntry(
    string Action,
    string SubjectType,
    string? SubjectId,
    Guid? ProductId = null,
    string? Summary = null,
    object? Before = null,
    object? After = null);

/// <summary>Append-only attributable audit trail. Implemented by the Governance module.</summary>
public interface IAuditWriter
{
    /// <summary>Persists the entry immediately. Call after the business commit succeeds; failures propagate.</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

public enum PauseScope { Publishing, Automation, Messaging, Ai }

/// <summary>Emergency stop switches (PRD 5.17). Checked by workers before any external action.</summary>
public interface IPauseSwitch
{
    Task<bool> IsPausedAsync(PauseScope scope, CancellationToken ct = default);
}
