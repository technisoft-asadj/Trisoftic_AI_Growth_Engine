using Microsoft.AspNetCore.Identity;

namespace TriSoftic.GrowthEngine.Modules.Identity.Domain;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() { Id = Guid.CreateVersion7(); }
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>When true the user can act on every product (still limited by permissions).</summary>
    public bool AllProducts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { Id = Guid.CreateVersion7(); }
    public ApplicationRole(string name) : this() { Name = name; NormalizedName = name.ToUpperInvariant(); }
}

/// <summary>Product assignment. ProductId references the Portfolio module by id only (no cross-schema FK, ADR-03).</summary>
public sealed class UserProductAccess
{
    public Guid UserId { get; set; }
    public Guid ProductId { get; set; }
}
