using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Portfolio.Domain;

public enum ProductKind { Game, BusinessSoftware, Other }
public enum Lifecycle { Idea, Development, Beta, Live, Retired }
public enum ProfileStatus { Draft, InReview, Approved, Rejected, Superseded }

public sealed class ProductCategory : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public ProductKind Kind { get; set; }
}

public sealed class Product : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public Guid CategoryId { get; set; }
    public ProductKind Kind { get; set; }
    public Lifecycle Lifecycle { get; set; } = Lifecycle.Idea;
    public string? Summary { get; set; }
    /// <summary>External identifiers, e.g. androidPackage, storeUrl.</summary>
    public Dictionary<string, string> Identifiers { get; set; } = [];
    public bool IsArchived { get; set; }
    /// <summary>Number of the latest Approved profile version; null until one is approved.</summary>
    public int? ApprovedProfileVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProductProfileVersion : IOrganizationScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public int Number { get; set; }
    public ProfileStatus Status { get; set; } = ProfileStatus.Draft;
    public string ContentJson { get; set; } = "{}";
    public string? ChangeNote { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }

    public void EnsureEditable()
    {
        if (Status != ProfileStatus.Draft) throw new IllegalTransitionException($"Profile v{Number} is {Status} and cannot be edited");
    }

    public void Submit(DateTimeOffset at)
    {
        if (Status != ProfileStatus.Draft) throw new IllegalTransitionException($"Only a Draft can be submitted (was {Status})");
        Status = ProfileStatus.InReview; SubmittedAt = at;
    }

    public void Withdraw()
    {
        if (Status != ProfileStatus.InReview) throw new IllegalTransitionException($"Only an InReview version can be withdrawn (was {Status})");
        Status = ProfileStatus.Draft; SubmittedAt = null;
    }

    public void Approve(Guid by, string? comment, DateTimeOffset at)
    {
        if (Status != ProfileStatus.InReview) throw new IllegalTransitionException($"Only an InReview version can be approved (was {Status})");
        Status = ProfileStatus.Approved; DecidedBy = by; DecidedAt = at; DecisionComment = comment;
    }

    public void Reject(Guid by, string? comment, DateTimeOffset at)
    {
        if (Status != ProfileStatus.InReview) throw new IllegalTransitionException($"Only an InReview version can be rejected (was {Status})");
        Status = ProfileStatus.Rejected; DecidedBy = by; DecidedAt = at; DecisionComment = comment;
    }

    public void ReturnForChanges(Guid by, string? comment, DateTimeOffset at)
    {
        if (Status != ProfileStatus.InReview) throw new IllegalTransitionException($"Only an InReview version can be returned (was {Status})");
        Status = ProfileStatus.Draft; DecidedBy = by; DecidedAt = at; DecisionComment = comment; SubmittedAt = null;
    }

    public void Supersede()
    {
        if (Status != ProfileStatus.Approved) throw new IllegalTransitionException($"Only an Approved version can be superseded (was {Status})");
        Status = ProfileStatus.Superseded;
    }
}
