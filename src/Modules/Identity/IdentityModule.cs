using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Identity.Domain;
using TriSoftic.GrowthEngine.Modules.Identity.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<IdentityModuleDbContext>(c, IdentityModuleDbContext.Schema);
        s.AddHttpContextAccessor();

        s.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = false;
                o.User.RequireUniqueEmail = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<IdentityModuleDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

        s.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies(o => { });
        s.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "te.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // fronted by TLS in staging/prod; forced Always via proxy settings
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            // API: never redirect, answer with status codes
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        s.AddAuthorization();
        s.AddScoped<ICurrentUser, CurrentUser>();
        s.AddScoped<IUserDirectory, UserDirectory>();
        return s;
    }

    /// <summary>
    /// CSRF defence for cookie auth: SameSite=Strict plus a required custom header on unsafe methods
    /// (a cross-site form or simple request cannot set it without a CORS preflight).
    /// </summary>
    public static IApplicationBuilder UseCsrfHeaderGuard(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var m = ctx.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m));
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api")
            && !ctx.Request.Path.StartsWithSegments("/api/v1/webhooks") // webhooks authenticate by signature
            && !ctx.Request.Path.StartsWithSegments("/api/v1/public")   // anonymous public forms (no cookie, so no CSRF surface)
            && !ctx.Request.Headers.ContainsKey("X-Requested-With"))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { title = "Missing X-Requested-With header" });
            return;
        }
        await next();
    });
}

