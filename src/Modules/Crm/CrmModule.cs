using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public static class CrmModule
{
    /// <summary>Name of the rate-limit policy the host must register for the anonymous lead-form route.</summary>
    public const string PublicFormsPolicy = "public-forms";

    public static IServiceCollection AddCrmModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<CrmDbContext>(c, CrmDbContext.Schema);
        s.AddScoped<IContactPolicy, ContactPolicy>();

        // Public lead forms are anonymous and embeddable on other sites: no credentials, POST only.
        s.AddCors(o => o.AddPolicy(PublicFormsPolicy, p => p.AllowAnyOrigin().WithMethods("POST").WithHeaders("Content-Type")));
        return s;
    }

    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        await SalesEndpoints.SeedAsync(scope.ServiceProvider.GetRequiredService<CrmDbContext>(), ct);
    }
}
