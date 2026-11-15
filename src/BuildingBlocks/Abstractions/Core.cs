namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

/// <summary>Correlation id flowing through API, worker, n8n and provider calls.</summary>
public interface ICorrelationContext { string CorrelationId { get; set; } }

public sealed class CorrelationContext : ICorrelationContext
{
    private string? _id;
    public string CorrelationId { get => _id ??= Guid.NewGuid().ToString("N"); set => _id = value; }
}

public static class OrganizationDefaults
{
    /// <summary>Single-organization deployment today; every business row carries the org id so tenancy can be added later (ADR-17).</summary>
    public static readonly Guid DefaultOrganizationId = new("00000000-0000-0000-0000-00000000a001");
}

public interface IOrganizationScoped { Guid OrganizationId { get; set; } }

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
}
