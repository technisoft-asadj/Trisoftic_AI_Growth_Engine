using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Identity.Domain;
using TriSoftic.GrowthEngine.Modules.Identity.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Identity;

/// <summary>Resolves the caller from the cookie principal. Product allow-list is read from the DB once per request.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, IdentityModuleDbContext db) : ICurrentUser
{
    private IReadOnlyCollection<Guid>? _products;
    private bool _productsLoaded;

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);

    public bool HasPermission(string permission) =>
        IsAuthenticated && Principal!.HasClaim(RoleCatalog.PermissionClaim, permission);

    private bool AllProducts => Principal?.HasClaim(RoleCatalog.AllProductsClaim, "true") == true;

    public async Task<IReadOnlyCollection<Guid>?> AccessibleProductIdsAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return Array.Empty<Guid>();
        if (AllProducts) return null;
        if (!_productsLoaded)
        {
            var uid = UserId!.Value;
            _products = await db.UserProductAccess.AsNoTracking().Where(x => x.UserId == uid).Select(x => x.ProductId).ToListAsync(ct);
            _productsLoaded = true;
        }
        return _products;
    }

    public async Task<bool> CanAccessProductAsync(Guid productId, CancellationToken ct = default)
    {
        var ids = await AccessibleProductIdsAsync(ct);
        return ids is null || ids.Contains(productId);
    }
}
