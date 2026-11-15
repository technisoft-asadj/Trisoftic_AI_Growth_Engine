using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Governance.Pause;

public sealed class PauseSwitch(GovernanceDbContext db) : IPauseSwitch
{
    public Task<bool> IsPausedAsync(PauseScope scope, CancellationToken ct = default)
    {
        var key = scope.ToString();
        return db.PauseFlags.AsNoTracking().Where(p => p.Scope == key).Select(p => p.IsPaused).FirstOrDefaultAsync(ct);
    }
}
