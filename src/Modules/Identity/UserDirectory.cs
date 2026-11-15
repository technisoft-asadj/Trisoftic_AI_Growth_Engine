using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Identity.Persistence;

namespace TriSoftic.GrowthEngine.Modules.Identity;

public sealed class UserDirectory(IdentityModuleDbContext db) : IUserDirectory
{
    public Task<bool> IsActiveUserAsync(Guid userId, CancellationToken ct = default) => db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, ct);

    public async Task<IReadOnlyList<DirectoryUser>> ListActiveAsync(CancellationToken ct = default) =>
        await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName).Select(u => new DirectoryUser(u.Id, u.DisplayName, u.Email!)).ToListAsync(ct);
}

public static class UserDirectoryEndpoints
{
    /// <summary>Names only, for pickers (owner, assignee). Available to anyone who works with leads, CRM or approvals.</summary>
    public static IEndpointRouteBuilder MapUserDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/directory/users", async (IUserDirectory dir, ICurrentUser u, CancellationToken ct) =>
            u.HasPermission(Permissions.CrmRead) || u.HasPermission(Permissions.LeadsManage) || u.HasPermission(Permissions.UsersManage)
                ? Results.Ok((await dir.ListActiveAsync(ct)).Select(x => new { x.Id, x.DisplayName }))
                : Results.Forbid()).RequireAuthorization().WithTags("Directory");
        return app;
    }
}
