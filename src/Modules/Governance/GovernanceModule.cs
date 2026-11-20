using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.Modules.Governance.Approvals;
using TriSoftic.GrowthEngine.Modules.Governance.Audit;
using TriSoftic.GrowthEngine.Modules.Governance.Pause;
using TriSoftic.GrowthEngine.Modules.Governance.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Governance;

public static class GovernanceModule
{
    public static IServiceCollection AddGovernanceModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<GovernanceDbContext>(c, GovernanceDbContext.Schema);
        s.AddScoped<IAuditWriter, AuditWriter>();
        s.AddScoped<IPauseSwitch, PauseSwitch>();
        s.AddScoped<IApprovalService, ApprovalService>();
        s.AddScoped<IAgentApprovals, AgentApprovals>();
        return s;
    }
}
