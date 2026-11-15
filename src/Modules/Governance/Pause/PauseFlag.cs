namespace TriSoftic.GrowthEngine.Modules.Governance.Pause;

public sealed class PauseFlag
{
    public string Scope { get; set; } = "";
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }
    public Guid? ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}
