using System.Reflection;
using NetArchTest.Rules;

namespace TriSoftic.GrowthEngine.Tests.Architecture;

/// <summary>ADR-03: modules talk through BuildingBlocks contracts only; no module may depend on another module.</summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(TriSoftic.GrowthEngine.Modules.Identity.IdentityModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Governance.GovernanceModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Portfolio.PortfolioModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Jobs.JobsModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Ai.AiModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Assets.AssetsModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Strategy.StrategyModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Content.ContentModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Crm.CrmModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Scheduling.SchedulingModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Automation.AutomationModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Publishing.PublishingModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Messaging.MessagingModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Analytics.AnalyticsModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Budget.BudgetModule).Assembly,
        typeof(TriSoftic.GrowthEngine.Modules.Planning.PlanningModule).Assembly,
    ];

    private static string ModuleName(Assembly a) => a.GetName().Name!.Split('.').Last();

    public static IEnumerable<object[]> Modules() => ModuleAssemblies.Select(a => new object[] { ModuleName(a) });

    [Theory, MemberData(nameof(Modules))]
    public void Module_does_not_depend_on_other_modules(string module)
    {
        var assembly = ModuleAssemblies.Single(a => ModuleName(a) == module);
        var others = ModuleAssemblies.Where(a => a != assembly).Select(a => $"TriSoftic.GrowthEngine.Modules.{ModuleName(a)}").ToArray();

        var result = Types.InAssembly(assembly).That().ResideInNamespaceStartingWith($"TriSoftic.GrowthEngine.Modules.{module}")
            .ShouldNot().HaveDependencyOnAny(others).GetResult();

        Assert.True(result.IsSuccessful, "Illegal cross-module dependencies: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void BuildingBlocks_does_not_depend_on_any_module()
    {
        var bb = typeof(TriSoftic.GrowthEngine.BuildingBlocks.Abstractions.IClock).Assembly;
        var result = Types.InAssembly(bb).ShouldNot().HaveDependencyOn("TriSoftic.GrowthEngine.Modules").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Only_the_Governance_module_writes_audit_events_or_approvals_directly()
    {
        foreach (var a in ModuleAssemblies.Where(a => ModuleName(a) != "Governance"))
        {
            var result = Types.InAssembly(a).ShouldNot().HaveDependencyOn("TriSoftic.GrowthEngine.Modules.Governance").GetResult();
            Assert.True(result.IsSuccessful, $"{ModuleName(a)} must use IAuditWriter/IApprovalService from BuildingBlocks");
        }
    }
}

