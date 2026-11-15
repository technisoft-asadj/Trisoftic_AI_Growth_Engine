using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriSoftic.GrowthEngine.Modules.Identity.Domain;
using TriSoftic.GrowthEngine.Modules.Identity.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Identity;

public sealed class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> users, RoleManager<ApplicationRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        if (user.AllProducts) id.AddClaim(new Claim(RoleCatalog.AllProductsClaim, "true"));
        if (!id.HasClaim(c => c.Type == ClaimTypes.Email) && user.Email is not null) id.AddClaim(new Claim(ClaimTypes.Email, user.Email));
        return id;
    }
}

public static class IdentitySeeder
{
    /// <summary>Idempotent: ensures roles + permission claims exist, and bootstraps the first administrator from configuration.</summary>
    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var s = scope.ServiceProvider;
        var roles = s.GetRequiredService<RoleManager<ApplicationRole>>();
        var users = s.GetRequiredService<UserManager<ApplicationUser>>();
        var log = s.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");

        foreach (var (name, perms) in RoleCatalog.Roles)
        {
            var role = await roles.FindByNameAsync(name);
            if (role is null)
            {
                var created = await roles.CreateAsync(new ApplicationRole(name));
                if (!created.Succeeded) throw new InvalidOperationException($"Cannot create role {name}: {string.Join(',', created.Errors.Select(e => e.Description))}");
                role = (await roles.FindByNameAsync(name))!;
            }
            var existing = (await roles.GetClaimsAsync(role)).Where(c => c.Type == RoleCatalog.PermissionClaim).Select(c => c.Value).ToHashSet();
            foreach (var p in perms.Where(p => !existing.Contains(p)))
                await roles.AddClaimAsync(role, new Claim(RoleCatalog.PermissionClaim, p));
        }

        var cfg = s.GetRequiredService<IConfiguration>();
        var email = cfg["Bootstrap:AdminEmail"];
        var password = cfg["Bootstrap:AdminPassword"];
        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password)
            && !await s.GetRequiredService<IdentityModuleDbContext>().Users.AnyAsync(ct))
        {
            var admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator", AllProducts = true };
            var r = await users.CreateAsync(admin, password);
            if (!r.Succeeded) throw new InvalidOperationException("Bootstrap admin failed: " + string.Join(',', r.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(admin, RoleCatalog.Administrator);
            log.LogWarning("Bootstrap administrator {Email} created. Rotate the password and remove Bootstrap settings.", email);
        }
    }
}
