using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.Modules.Identity.Domain;

namespace TriSoftic.GrowthEngine.Modules.Identity.Persistence;

public sealed class IdentityModuleDbContext(DbContextOptions<IdentityModuleDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public const string Schema = "identity";
    public DbSet<UserProductAccess> UserProductAccess => Set<UserProductAccess>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasDefaultSchema(Schema);
        b.Entity<ApplicationUser>(e => { e.ToTable("users"); e.Property(x => x.DisplayName).HasMaxLength(200); });
        b.Entity<ApplicationRole>().ToTable("roles");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().ToTable("user_roles");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("user_claims");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("user_logins");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("user_tokens");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>().ToTable("role_claims");
        b.Entity<UserProductAccess>(e =>
        {
            e.ToTable("user_product_access");
            e.HasKey(x => new { x.UserId, x.ProductId });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProductId);
        });
    }
}
