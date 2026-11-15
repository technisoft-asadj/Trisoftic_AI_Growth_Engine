namespace TriSoftic.GrowthEngine.BuildingBlocks.Security;

/// <summary>Action-level permissions. Combined with product scope (ICurrentUser.CanAccessProduct) on every command (ADR-07).</summary>
public static class Permissions
{
    public const string UsersManage = "users.manage";
    public const string AuditRead = "audit.read";
    public const string JobsManage = "jobs.manage";
    public const string GovernancePause = "governance.pause";
    public const string ProductsRead = "products.read";
    public const string ProductsWrite = "products.write";
    public const string ProductProfileApprove = "products.profile.approve";
    public const string AssetsRead = "assets.read";
    public const string AssetsWrite = "assets.write";
    public const string AiGenerate = "ai.generate";
    public const string ContentWrite = "content.write";
    public const string ContentApprove = "content.approve";
    public const string CampaignsManage = "campaigns.manage";
    public const string PublishingManage = "publishing.manage";
    public const string AccountsConnect = "accounts.connect";
    public const string CrmRead = "crm.read";
    public const string CrmWrite = "crm.write";
    public const string LeadsManage = "leads.manage";
    public const string SalesManage = "sales.manage";
    public const string AppointmentsManage = "appointments.manage";
    public const string AutomationManage = "automation.manage";
    public const string AnalyticsRead = "analytics.read";
    public const string AnalyticsImport = "analytics.import";
    public const string BudgetRead = "budget.read";
    public const string BudgetPropose = "budget.propose";
    public const string BudgetApprove = "budget.approve";

    public static readonly IReadOnlyList<string> All =
        typeof(Permissions).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? Email { get; }
    bool HasPermission(string permission);
    /// <summary>True when the user may act on the given product (all-products flag or explicit assignment).</summary>
    Task<bool> CanAccessProductAsync(Guid productId, CancellationToken ct = default);
    /// <summary>null = all products; otherwise the explicit allow-list (for query filtering).</summary>
    Task<IReadOnlyCollection<Guid>?> AccessibleProductIdsAsync(CancellationToken ct = default);
}
