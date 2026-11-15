using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public static class CrmModule
{
    public static IServiceCollection AddCrmModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<CrmDbContext>(c, CrmDbContext.Schema);
        s.AddScoped<IContactPolicy, ContactPolicy>();
        return s;
    }
}
