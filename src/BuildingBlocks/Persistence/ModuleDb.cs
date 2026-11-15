using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TriSoftic.GrowthEngine.BuildingBlocks.Persistence;

public static class ModuleDb
{
    /// <summary>Each module owns one Postgres schema and its own migrations history table (ADR-03).</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, IConfiguration config, string schema)
        where TContext : DbContext
    {
        var cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is required");
        services.AddDbContext<TContext>(o => o
            .UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__ef_migrations", schema))
            .AddInterceptors(new OrganizationStampInterceptor()));
        return services;
    }
}
