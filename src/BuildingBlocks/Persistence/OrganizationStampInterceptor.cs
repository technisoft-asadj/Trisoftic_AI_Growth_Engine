using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.BuildingBlocks.Persistence;

/// <summary>Stamps the default OrganizationId on new IOrganizationScoped rows (ADR-17).</summary>
public sealed class OrganizationStampInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Stamp(e.Context);
        return base.SavingChangesAsync(e, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> result)
    {
        Stamp(e.Context);
        return base.SavingChanges(e, result);
    }

    private static void Stamp(DbContext? ctx)
    {
        if (ctx is null) return;
        foreach (var entry in ctx.ChangeTracker.Entries<IOrganizationScoped>())
            if (entry.State == EntityState.Added && entry.Entity.OrganizationId == Guid.Empty)
                entry.Entity.OrganizationId = OrganizationDefaults.DefaultOrganizationId;
    }
}
