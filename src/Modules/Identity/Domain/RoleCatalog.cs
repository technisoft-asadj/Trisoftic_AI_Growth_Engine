using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using P = TriSoftic.GrowthEngine.BuildingBlocks.Security.Permissions;

namespace TriSoftic.GrowthEngine.Modules.Identity.Domain;

/// <summary>PRD section 4 roles mapped to permissions. Seeded idempotently at startup; new permissions are added, never silently removed.</summary>
public static class RoleCatalog
{
    public const string PermissionClaim = "perm";
    public const string AllProductsClaim = "all_products";

    public const string Administrator = "Platform Administrator";
    public const string MarketingManager = "Marketing Manager";
    public const string ProductManager = "Product Manager";
    public const string ContentCreator = "Content Creator";
    public const string Approver = "Approver";
    public const string SalesRepresentative = "Sales Representative";
    public const string Analyst = "Analyst";
    public const string Viewer = "Read-only Viewer";

    public static readonly IReadOnlyDictionary<string, string[]> Roles = new Dictionary<string, string[]>
    {
        [Administrator] = [.. P.All],
        [MarketingManager] =
        [
            P.ProductsRead, P.AssetsRead, P.AssetsWrite, P.AiGenerate, P.ContentWrite, P.CampaignsManage, P.PublishingManage,
            P.CrmRead, P.AnalyticsRead, P.AnalyticsImport, P.BudgetRead, P.BudgetPropose,
        ],
        [ProductManager] =
        [
            P.ProductsRead, P.ProductsWrite, P.AssetsRead, P.AssetsWrite, P.AiGenerate, P.ContentWrite, P.AnalyticsRead,
        ],
        [ContentCreator] = [P.ProductsRead, P.AssetsRead, P.AiGenerate, P.ContentWrite],
        [Approver] = [P.ProductsRead, P.AssetsRead, P.ProductProfileApprove, P.ContentApprove, P.BudgetRead],
        [SalesRepresentative] =
        [
            P.ProductsRead, P.CrmRead, P.CrmWrite, P.LeadsManage, P.SalesManage, P.AppointmentsManage,
        ],
        [Analyst] = [P.ProductsRead, P.CrmRead, P.AnalyticsRead, P.AnalyticsImport, P.BudgetRead],
        [Viewer] = [P.ProductsRead, P.AssetsRead, P.CrmRead, P.AnalyticsRead, P.BudgetRead],
    };
}
