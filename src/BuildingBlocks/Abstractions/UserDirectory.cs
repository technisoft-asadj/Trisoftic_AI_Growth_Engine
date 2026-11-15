namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

public sealed record DirectoryUser(Guid Id, string DisplayName, string Email);

/// <summary>Read-only view of platform users for other modules (assignment, owners). Implemented by Identity.</summary>
public interface IUserDirectory
{
    Task<bool> IsActiveUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryUser>> ListActiveAsync(CancellationToken ct = default);
}
