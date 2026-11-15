using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Governance.Audit;

public sealed class AuditEvent : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string Action { get; set; } = "";
    public string SubjectType { get; set; } = "";
    public string? SubjectId { get; set; }
    public Guid? ProductId { get; set; }
    public string? Summary { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? CorrelationId { get; set; }
}
