using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Identity.Domain;
using TriSoftic.GrowthEngine.Modules.Identity.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Identity;

public static class IdentityEndpoints
{
    public sealed record LoginRequest(string Email, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record MeDto(Guid Id, string Email, string DisplayName, string[] Roles, string[] Permissions, bool AllProducts, Guid[] ProductIds);
    public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsActive, bool AllProducts, string[] Roles, Guid[] ProductIds);
    public sealed record CreateUserRequest(string Email, string DisplayName, string Password, string[] Roles, bool AllProducts, Guid[]? ProductIds);
    public sealed record SetRolesRequest(string[] Roles);
    public sealed record SetProductAccessRequest(bool AllProducts, Guid[]? ProductIds);

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");

        auth.MapPost("/login", async (LoginRequest req, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditWriter audit) =>
        {
            var user = await users.FindByEmailAsync(req.Email ?? "");
            if (user is null || !user.IsActive) return Results.Problem(statusCode: 401, title: "Invalid credentials");
            var r = await signIn.PasswordSignInAsync(user, req.Password ?? "", isPersistent: false, lockoutOnFailure: true);
            if (r.IsLockedOut) return Results.Problem(statusCode: 429, title: "Account temporarily locked");
            if (!r.Succeeded) return Results.Problem(statusCode: 401, title: "Invalid credentials");
            return Results.NoContent();
        });

        auth.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); })
            .RequireAuthorization();

        // Signed-in users change their own password. Other sessions are signed out (the security stamp changes); this one stays signed in.
        auth.MapPost("/change-password", async (ChangePasswordRequest req, ICurrentUser cu, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditWriter audit) =>
        {
            var user = await users.FindByIdAsync(cu.UserId!.Value.ToString());
            if (user is null || !user.IsActive) return Results.Unauthorized();
            if (string.IsNullOrEmpty(req.CurrentPassword) || string.IsNullOrEmpty(req.NewPassword)) return Results.Problem(statusCode: 400, title: "Enter your current and new password");
            var r = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
            if (!r.Succeeded)
            {
                var wrong = r.Errors.Any(e => e.Code == "PasswordMismatch");
                return Results.Problem(statusCode: 400, title: wrong ? "The current password is not correct" : "The new password is not allowed", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors.Where(e => e.Code != "PasswordMismatch").Select(e => e.Description).ToArray() });
            }
            await signIn.RefreshSignInAsync(user);
            await audit.WriteAsync(new AuditEntry("identity.password-changed", "User", user.Id.ToString(), null, user.Email), default);
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapGet("/me", async (ICurrentUser cu, UserManager<ApplicationUser> users, IHttpContextAccessor ctx, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(cu.UserId!.Value.ToString());
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var p = ctx.HttpContext!.User;
            var roles = p.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).OrderBy(x => x).ToArray();
            var perms = p.FindAll(RoleCatalog.PermissionClaim).Select(c => c.Value).Distinct().OrderBy(x => x).ToArray();
            var ids = await cu.AccessibleProductIdsAsync(ct);
            return Results.Ok(new MeDto(user.Id, user.Email!, user.DisplayName, roles, perms, ids is null, ids?.ToArray() ?? []));
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/v1/users").WithTags("Users").RequireAuthorization();

        admin.MapGet("/", async (ICurrentUser cu, IdentityModuleDbContext db, UserManager<ApplicationUser> um, CancellationToken ct) =>
        {
            if (!cu.HasPermission(Permissions.UsersManage)) return Results.Forbid();
            var list = await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct);
            var access = await db.UserProductAccess.AsNoTracking().ToListAsync(ct);
            var roleMap = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync(ct);
            return Results.Ok(list.Select(u => new UserDto(u.Id, u.Email!, u.DisplayName, u.IsActive, u.AllProducts,
                roleMap.Where(x => x.UserId == u.Id).Select(x => x.Name!).OrderBy(x => x).ToArray(),
                access.Where(a => a.UserId == u.Id).Select(a => a.ProductId).ToArray())));
        });

        admin.MapGet("/roles", (ICurrentUser cu) =>
            cu.HasPermission(Permissions.UsersManage)
                ? Results.Ok(RoleCatalog.Roles.Select(r => new { Name = r.Key, Permissions = r.Value }))
                : Results.Forbid());

        admin.MapPost("/", async (CreateUserRequest req, ICurrentUser cu, UserManager<ApplicationUser> um, IdentityModuleDbContext db, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!cu.HasPermission(Permissions.UsersManage)) return Results.Forbid();
            if (req.Roles is null || req.Roles.Length == 0 || req.Roles.Any(r => !RoleCatalog.Roles.ContainsKey(r)))
                return Results.Problem(statusCode: 400, title: "Unknown or missing role");
            var user = new ApplicationUser { UserName = req.Email, Email = req.Email, EmailConfirmed = true, DisplayName = req.DisplayName, AllProducts = req.AllProducts };
            var created = await um.CreateAsync(user, req.Password);
            if (!created.Succeeded) return Results.ValidationProblem(created.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
            await um.AddToRolesAsync(user, req.Roles);
            if (!req.AllProducts && req.ProductIds is { Length: > 0 })
            {
                db.UserProductAccess.AddRange(req.ProductIds.Distinct().Select(p => new UserProductAccess { UserId = user.Id, ProductId = p }));
                await db.SaveChangesAsync(ct);
            }
            await audit.WriteAsync(new AuditEntry("user.created", "User", user.Id.ToString(), Summary: user.Email,
                After: new { req.Roles, req.AllProducts, req.ProductIds }), ct);
            return Results.Created($"/api/v1/users/{user.Id}", new { user.Id });
        });

        admin.MapPut("/{id:guid}/roles", async (Guid id, SetRolesRequest req, ICurrentUser cu, UserManager<ApplicationUser> um, IAuditWriter audit) =>
        {
            if (!cu.HasPermission(Permissions.UsersManage)) return Results.Forbid();
            if (req.Roles is null || req.Roles.Length == 0 || req.Roles.Any(r => !RoleCatalog.Roles.ContainsKey(r)))
                return Results.Problem(statusCode: 400, title: "Unknown or missing role");
            var user = await um.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var before = (await um.GetRolesAsync(user)).ToArray();
            await um.RemoveFromRolesAsync(user, before.Except(req.Roles));
            await um.AddToRolesAsync(user, req.Roles.Except(before));
            await um.UpdateSecurityStampAsync(user);
            await audit.WriteAsync(new AuditEntry("user.roles.changed", "User", id.ToString(), Summary: user.Email, Before: new { Roles = before }, After: new { req.Roles }));
            return Results.NoContent();
        });

        admin.MapPut("/{id:guid}/product-access", async (Guid id, SetProductAccessRequest req, ICurrentUser cu, UserManager<ApplicationUser> um, IdentityModuleDbContext db, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!cu.HasPermission(Permissions.UsersManage)) return Results.Forbid();
            var user = await um.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var beforeIds = await db.UserProductAccess.Where(x => x.UserId == id).Select(x => x.ProductId).ToListAsync(ct);
            var wanted = req.AllProducts ? [] : (req.ProductIds ?? []).Distinct().ToArray();
            db.UserProductAccess.RemoveRange(db.UserProductAccess.Where(x => x.UserId == id));
            db.UserProductAccess.AddRange(wanted.Select(p => new UserProductAccess { UserId = id, ProductId = p }));
            await db.SaveChangesAsync(ct);
            var beforeAll = user.AllProducts;
            user.AllProducts = req.AllProducts;
            await um.UpdateAsync(user);
            await um.UpdateSecurityStampAsync(user);
            await audit.WriteAsync(new AuditEntry("user.product-access.changed", "User", id.ToString(), Summary: user.Email,
                Before: new { AllProducts = beforeAll, ProductIds = beforeIds }, After: new { req.AllProducts, ProductIds = wanted }), ct);
            return Results.NoContent();
        });

        admin.MapPost("/{id:guid}/active/{active:bool}", async (Guid id, bool active, ICurrentUser cu, UserManager<ApplicationUser> um, IAuditWriter audit) =>
        {
            if (!cu.HasPermission(Permissions.UsersManage)) return Results.Forbid();
            if (!active && cu.UserId == id) return Results.Problem(statusCode: 400, title: "You cannot deactivate yourself");
            var user = await um.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            user.IsActive = active;
            await um.UpdateAsync(user);
            await um.UpdateSecurityStampAsync(user);
            await audit.WriteAsync(new AuditEntry(active ? "user.activated" : "user.deactivated", "User", id.ToString(), Summary: user.Email));
            return Results.NoContent();
        });

        app.MapUserDirectoryEndpoints();
        return app;
    }
}
