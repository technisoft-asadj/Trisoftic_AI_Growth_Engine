using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public static partial class SalesEndpoints
{
    public sealed record StageIn(Guid? Id, string Name, StageKind Kind, int Probability);
    public sealed record PipelineIn(string Name, Guid? ProductId, StageIn[] Stages);
    public sealed record OppIn(Guid PipelineId, Guid ContactId, Guid ProductId, Guid? CampaignId, string Name, decimal Value, string? Currency, DateOnly? ExpectedCloseDate,
        int? ProbabilityOverride, Guid? OwnerUserId, string? NextStep, string? Source);
    public sealed record OppUpdate(string? Name, decimal? Value, string? Currency, DateOnly? ExpectedCloseDate, int? ProbabilityOverride, string? NextStep);
    public sealed record MoveIn(Guid StageId, string? Reason, decimal? Revenue, string? Note);
    public sealed record ReopenIn(Guid StageId);
    public sealed record OwnerIn(Guid? UserId);
    public sealed record ActivityIn(ActivityType Type, string Summary, DateTimeOffset? DueAt);
    public sealed record ConvertIn(Guid? PipelineId, string? Name, decimal Value, string? Currency, DateOnly? ExpectedCloseDate);

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyRx();

    private static IResult Illegal(IllegalTransitionException ex) => Results.Problem(statusCode: 409, title: ex.Message);

    private static async Task<Opportunity?> FindOpp(CrmDbContext db, ICurrentUser u, Guid id, string permission, CancellationToken ct)
    {
        if (!u.HasPermission(permission)) return null;
        var o = await db.Opportunities.FirstOrDefaultAsync(x => x.Id == id, ct);
        return o is not null && await u.CanAccessProductAsync(o.ProductId, ct) ? o : null;
    }

    private static object OppDto(Opportunity o, PipelineStage s) => new
    {
        o.Id, o.PipelineId, o.StageId, stage = s.Name, stageKind = s.Kind, o.ContactId, o.CompanyId, o.ProductId, o.LeadId, o.CampaignId, o.Name, o.Value, o.Currency,
        probability = OpportunityRules.EffectiveProbability(o, s), o.ExpectedCloseDate, o.OwnerUserId, o.Source, o.NextStep, o.Status, o.WonLostReason, o.RevenueAmount, o.ClosedAt, o.CreatedAt, o.UpdatedAt,
    };

    /// <summary>Creates a deal in the first stage of the pipeline. Shared by direct creation and lead conversion.</summary>
    private static async Task<Result<(Opportunity Opp, PipelineStage Stage)>> CreateAsync(CrmDbContext db, ICurrentUser u, IUserDirectory dir, OppIn r, Guid? leadId, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 300) return new Error("opp.name", "A deal name is required");
        if (r.Value is < 0 or > 1_000_000_000_000m) return new Error("opp.value", "The value is out of range");
        var currency = (r.Currency ?? "USD").Trim().ToUpperInvariant();
        if (!CurrencyRx().IsMatch(currency)) return new Error("opp.currency", "Use a 3-letter currency code such as USD or PKR");
        if (r.ProbabilityOverride is < 0 or > 100) return new Error("opp.probability", "Probability is between 0 and 100");
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PipelineId && !p.IsArchived, ct);
        if (pipeline is null) return new Error("opp.pipeline", "Pipeline not found");
        if (pipeline.ProductId is not null && pipeline.ProductId != r.ProductId) return new Error("opp.pipeline", "That pipeline belongs to a different product");
        var contact = await (await CrmEndpoints.VisibleAsync(db, u, false, ct)).FirstOrDefaultAsync(c => c.Id == r.ContactId, ct);
        if (contact is null) return new Error("opp.contact", "Contact not found");
        if (r.OwnerUserId is { } ow && !await dir.IsActiveUserAsync(ow, ct)) return new Error("opp.owner", "Unknown or inactive owner");
        var first = await db.Stages.AsNoTracking().Where(s => s.PipelineId == pipeline.Id && s.Kind == StageKind.Open).OrderBy(s => s.Order).FirstAsync(ct);

        var o = new Opportunity
        {
            PipelineId = pipeline.Id, StageId = first.Id, ContactId = contact.Id, CompanyId = contact.CompanyId, ProductId = r.ProductId, LeadId = leadId, CampaignId = r.CampaignId,
            Name = r.Name.Trim(), Value = r.Value, Currency = currency, ProbabilityOverride = r.ProbabilityOverride, ExpectedCloseDate = r.ExpectedCloseDate,
            OwnerUserId = r.OwnerUserId ?? u.UserId, NextStep = r.NextStep, Source = r.Source, CreatedAt = now, UpdatedAt = now,
        };
        db.Opportunities.Add(o);
        db.StageChanges.Add(new OpportunityStageChange { OpportunityId = o.Id, FromStageId = null, ToStageId = first.Id, Note = "Created", ChangedBy = u.UserId, ChangedAt = now });
        if (!await db.Associations.AnyAsync(a => a.ContactId == contact.Id && a.ProductId == r.ProductId && a.CampaignId == r.CampaignId, ct))
            db.Associations.Add(new ContactAssociation { ContactId = contact.Id, ProductId = r.ProductId, CampaignId = r.CampaignId });
        return (o, first);
    }

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/crm").RequireAuthorization().WithTags("Sales");
        MapPipelines(g);
        MapOpportunities(g);
        return app;
    }

    // ---------- pipelines ----------
    private static void MapPipelines(RouteGroupBuilder g)
    {
        g.MapGet("/pipelines", async (CrmDbContext db, ICurrentUser u, Guid? productId, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var q = db.Pipelines.AsNoTracking().Where(p => !p.IsArchived);
            if (productId is not null) q = q.Where(p => p.ProductId == null || p.ProductId == productId);
            var ps = await q.OrderBy(p => p.Name).ToListAsync(ct);
            var ids = ps.Select(p => p.Id).ToList();
            var stages = await db.Stages.AsNoTracking().Where(s => ids.Contains(s.PipelineId)).OrderBy(s => s.Order).ToListAsync(ct);
            return Results.Ok(ps.Select(p => new { p.Id, p.Name, p.ProductId, stages = stages.Where(s => s.PipelineId == p.Id).Select(s => new { s.Id, s.Name, s.Order, s.Probability, s.Kind }) }));
        });

        g.MapPost("/pipelines", async (PipelineIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.SalesManage)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) return Results.Problem(statusCode: 400, title: "A pipeline name is required");
            if (req.ProductId is { } pid && !await u.CanAccessProductAsync(pid, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inaccessible product");
            if (OpportunityRules.ValidateStages(req.Stages.Select(s => (s.Name, s.Kind, s.Probability)).ToList()) is { } err) return Results.Problem(statusCode: 400, title: err);
            var p = new Pipeline { Name = req.Name.Trim(), ProductId = req.ProductId, CreatedAt = clock.UtcNow };
            db.Pipelines.Add(p);
            for (var i = 0; i < req.Stages.Length; i++) db.Stages.Add(new PipelineStage { PipelineId = p.Id, Name = req.Stages[i].Name.Trim(), Order = i, Probability = req.Stages[i].Probability, Kind = req.Stages[i].Kind });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.pipeline.created", "Pipeline", p.Id.ToString(), p.ProductId, p.Name, After: new { Stages = req.Stages.Length }), ct);
            return Results.Created($"/api/v1/crm/pipelines/{p.Id}", new { p.Id });
        });

        // Replace the stage list: existing stages are matched by id; stages that disappear must hold no deals; a stage's kind cannot change.
        g.MapPut("/pipelines/{id:guid}", async (Guid id, PipelineIn req, CrmDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.SalesManage)) return Results.Forbid();
            var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem(statusCode: 400, title: "A pipeline name is required");
            if (OpportunityRules.ValidateStages(req.Stages.Select(s => (s.Name, s.Kind, s.Probability)).ToList()) is { } err) return Results.Problem(statusCode: 400, title: err);
            var existing = await db.Stages.Where(s => s.PipelineId == id).ToListAsync(ct);
            var keep = req.Stages.Where(s => s.Id is not null).Select(s => s.Id!.Value).ToHashSet();
            foreach (var s in req.Stages.Where(s => s.Id is not null))
            {
                var cur = existing.FirstOrDefault(e => e.Id == s.Id);
                if (cur is null) return Results.Problem(statusCode: 400, title: "Unknown stage id");
                if (cur.Kind != s.Kind) return Results.Problem(statusCode: 400, title: $"The kind of stage '{cur.Name}' cannot change");
            }
            foreach (var gone in existing.Where(e => !keep.Contains(e.Id)))
            {
                if (await db.Opportunities.AnyAsync(o => o.StageId == gone.Id, ct)) return Results.Problem(statusCode: 409, title: $"Stage '{gone.Name}' still has deals; move them first");
                db.Stages.Remove(gone);
            }
            for (var i = 0; i < req.Stages.Length; i++)
            {
                var s = req.Stages[i];
                var cur = s.Id is null ? null : existing.First(e => e.Id == s.Id);
                if (cur is null) db.Stages.Add(new PipelineStage { PipelineId = id, Name = s.Name.Trim(), Order = i, Probability = s.Probability, Kind = s.Kind });
                else { cur.Name = s.Name.Trim(); cur.Order = i; cur.Probability = s.Probability; }
            }
            p.Name = req.Name.Trim();
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.pipeline.updated", "Pipeline", id.ToString(), p.ProductId, p.Name), ct);
            return Results.NoContent();
        });

        g.MapGet("/pipelines/{id:guid}/board", async (Guid id, CrmDbContext db, ICurrentUser u, Guid? productId, Guid? ownerId, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var p = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            var stages = await db.Stages.AsNoTracking().Where(s => s.PipelineId == id).OrderBy(s => s.Order).ToListAsync(ct);
            var q = db.Opportunities.AsNoTracking().Where(o => o.PipelineId == id);
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(o => scope.Contains(o.ProductId));
            if (productId is not null) q = q.Where(o => o.ProductId == productId);
            if (ownerId is not null) q = q.Where(o => o.OwnerUserId == ownerId);
            // Closed deals older than 30 days drop off the board; they remain in lists and reports.
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            q = q.Where(o => o.Status == OpportunityStatus.Open || o.ClosedAt >= cutoff);
            var opps = await q.OrderByDescending(o => o.UpdatedAt).Take(1000).ToListAsync(ct);
            var cids = opps.Select(o => o.ContactId).Distinct().ToList();
            var names = await db.Contacts.AsNoTracking().Where(c => cids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
            return Results.Ok(new
            {
                pipeline = new { p.Id, p.Name },
                columns = stages.Select(s =>
                {
                    var inStage = opps.Where(o => o.StageId == s.Id).ToList();
                    return new
                    {
                        stage = new { s.Id, s.Name, s.Kind, s.Probability }, count = inStage.Count,
                        // Mixed currencies are never added together.
                        totalsByCurrency = inStage.GroupBy(o => o.Currency).Select(c => new { currency = c.Key, value = c.Sum(o => o.Value), weighted = Math.Round(c.Sum(o => o.Value * OpportunityRules.EffectiveProbability(o, s) / 100m), 2) }),
                        items = inStage.Take(200).Select(o => new { o.Id, o.Name, o.Value, o.Currency, probability = OpportunityRules.EffectiveProbability(o, s), o.ExpectedCloseDate, o.OwnerUserId, contactName = names.GetValueOrDefault(o.ContactId), o.NextStep }),
                    };
                }),
            });
        });
    }

    // ---------- opportunities ----------
    private static void MapOpportunities(RouteGroupBuilder g)
    {
        g.MapGet("/opportunities", async (CrmDbContext db, ICurrentUser u, Guid? pipelineId, OpportunityStatus? status, Guid? productId, Guid? ownerId, Guid? campaignId, string? cursor, int? take, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var q = db.Opportunities.AsNoTracking().AsQueryable();
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(o => scope.Contains(o.ProductId));
            if (pipelineId is not null) q = q.Where(o => o.PipelineId == pipelineId);
            if (status is not null) q = q.Where(o => o.Status == status);
            if (productId is not null) q = q.Where(o => o.ProductId == productId);
            if (ownerId is not null) q = q.Where(o => o.OwnerUserId == ownerId);
            if (campaignId is not null) q = q.Where(o => o.CampaignId == campaignId);
            if (Guid.TryParse(cursor, out var cur)) q = q.Where(o => o.Id.CompareTo(cur) < 0);
            var n = Math.Clamp(take ?? 50, 1, 200);
            var rows = await q.OrderByDescending(o => o.Id).Take(n + 1).ToListAsync(ct);
            var sids = rows.Select(r => r.StageId).Distinct().ToList();
            var stages = await db.Stages.AsNoTracking().Where(s => sids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
            return Results.Ok(new { items = rows.Take(n).Select(o => OppDto(o, stages[o.StageId])), nextCursor = rows.Count > n ? rows[n - 1].Id.ToString() : null });
        });

        g.MapGet("/opportunities/{id:guid}", async (Guid id, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var o = await FindOpp(db, u, id, Permissions.CrmRead, ct);
            if (o is null) return Results.NotFound();
            var stage = await db.Stages.AsNoTracking().FirstAsync(s => s.Id == o.StageId, ct);
            var stageNames = await db.Stages.AsNoTracking().Where(s => s.PipelineId == o.PipelineId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            var history = await db.StageChanges.AsNoTracking().Where(h => h.OpportunityId == id).OrderBy(h => h.ChangedAt).ThenBy(h => h.Id).ToListAsync(ct);
            var acts = await db.Activities.AsNoTracking().Where(a => a.OpportunityId == id).OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
            var contact = await db.Contacts.AsNoTracking().Where(c => c.Id == o.ContactId).Select(c => new { c.Id, c.DisplayName }).FirstAsync(ct);
            return Results.Ok(new
            {
                opportunity = OppDto(o, stage), contact,
                history = history.Select(h => new { from = h.FromStageId is { } f ? stageNames.GetValueOrDefault(f) : null, to = stageNames.GetValueOrDefault(h.ToStageId), h.Note, h.ChangedBy, h.ChangedAt }),
                activities = acts.Select(a => new { a.Id, a.Type, a.Summary, a.DueAt, a.CompletedAt, a.OwnerUserId, a.CreatedAt }),
            });
        });

        g.MapPost("/opportunities", async (OppIn req, CrmDbContext db, ICurrentUser u, IUserDirectory dir, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.SalesManage) || !await u.CanAccessProductAsync(req.ProductId, ct)) return Results.NotFound();
            var res = await CreateAsync(db, u, dir, req, null, clock.UtcNow, ct);
            if (!res.IsSuccess) return Results.Problem(statusCode: 400, title: res.Error!.Message);
            var (o, stage) = res.Value;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.opportunity.created", "Opportunity", o.Id.ToString(), o.ProductId, o.Name, After: new { o.Value, o.Currency, Stage = stage.Name }), ct);
            return Results.Created($"/api/v1/crm/opportunities/{o.Id}", OppDto(o, stage));
        });

        g.MapPost("/leads/{id:guid}/convert", async (Guid id, ConvertIn req, CrmDbContext db, ICurrentUser u, IUserDirectory dir, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.SalesManage)) return Results.NotFound();
            var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (lead is null || !await u.CanAccessProductAsync(lead.ProductId, ct)) return Results.NotFound();
            if (lead.Status != LeadStatus.Qualified) return Results.Problem(statusCode: 409, title: "Only a Qualified lead can be converted to an opportunity");
            var contact = await db.Contacts.AsNoTracking().FirstAsync(c => c.Id == lead.ContactId, ct);
            var pipelineId = req.PipelineId ?? await db.Pipelines.AsNoTracking().Where(p => !p.IsArchived && (p.ProductId == lead.ProductId || p.ProductId == null))
                .OrderByDescending(p => p.ProductId == lead.ProductId).ThenBy(p => p.CreatedAt).Select(p => p.Id).FirstOrDefaultAsync(ct);
            if (pipelineId == Guid.Empty) return Results.Problem(statusCode: 400, title: "No pipeline is available for this product");
            var now = clock.UtcNow;
            var res = await CreateAsync(db, u, dir, new OppIn(pipelineId, lead.ContactId, lead.ProductId, lead.CampaignId, string.IsNullOrWhiteSpace(req.Name) ? $"{contact.DisplayName} - deal" : req.Name,
                req.Value, req.Currency, req.ExpectedCloseDate, null, lead.OwnerUserId ?? u.UserId, null, lead.Source), lead.Id, now, ct);
            if (!res.IsSuccess) return Results.Problem(statusCode: 400, title: res.Error!.Message);
            var (o, stage) = res.Value;
            lead.Convert(o.Id, now); lead.UpdatedAt = now;
            db.Interactions.Add(new Interaction { ContactId = lead.ContactId, Type = InteractionType.System, ProductId = lead.ProductId, Summary = $"Lead converted to deal '{o.Name}'", OccurredAt = now, CreatedBy = u.UserId });
            await db.SaveChangesAsync(ct);   // lead + deal + history commit together
            await audit.WriteAsync(new AuditEntry("crm.lead.converted", "Lead", id.ToString(), lead.ProductId, o.Name, After: new { OpportunityId = o.Id, o.Value, o.Currency }), ct);
            return Results.Created($"/api/v1/crm/opportunities/{o.Id}", OppDto(o, stage));
        });

        g.MapPut("/opportunities/{id:guid}", async (Guid id, OppUpdate req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var o = await FindOpp(db, u, id, Permissions.SalesManage, ct);
            if (o is null) return Results.NotFound();
            if (o.Status != OpportunityStatus.Open) return Results.Problem(statusCode: 409, title: "A closed deal cannot be edited; reopen it first");
            var before = new { o.Name, o.Value, o.Currency, o.ProbabilityOverride, o.ExpectedCloseDate };
            if (!string.IsNullOrWhiteSpace(req.Name)) o.Name = req.Name.Trim();
            if (req.Value is { } v) { if (v is < 0 or > 1_000_000_000_000m) return Results.Problem(statusCode: 400, title: "The value is out of range"); o.Value = v; }
            if (req.Currency is not null) { var c = req.Currency.Trim().ToUpperInvariant(); if (!CurrencyRx().IsMatch(c)) return Results.Problem(statusCode: 400, title: "Use a 3-letter currency code"); o.Currency = c; }
            if (req.ProbabilityOverride is { } p) { if (p is < 0 or > 100) return Results.Problem(statusCode: 400, title: "Probability is between 0 and 100"); o.ProbabilityOverride = p; }
            if (req.ExpectedCloseDate is not null) o.ExpectedCloseDate = req.ExpectedCloseDate;
            if (req.NextStep is not null) o.NextStep = req.NextStep;
            o.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.opportunity.updated", "Opportunity", id.ToString(), o.ProductId, o.Name, Before: before, After: new { o.Name, o.Value, o.Currency, o.ProbabilityOverride, o.ExpectedCloseDate }), ct);
            return Results.NoContent();
        });

        g.MapPut("/opportunities/{id:guid}/owner", async (Guid id, OwnerIn req, CrmDbContext db, ICurrentUser u, IUserDirectory dir, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var o = await FindOpp(db, u, id, Permissions.SalesManage, ct);
            if (o is null) return Results.NotFound();
            if (req.UserId is { } uid && !await dir.IsActiveUserAsync(uid, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inactive user");
            var before = o.OwnerUserId; o.OwnerUserId = req.UserId; o.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.opportunity.owner-changed", "Opportunity", id.ToString(), o.ProductId, o.Name, Before: new { Owner = before }, After: new { Owner = req.UserId }), ct);
            return Results.NoContent();
        });

        g.MapPost("/opportunities/{id:guid}/stage", async (Guid id, MoveIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var o = await FindOpp(db, u, id, Permissions.SalesManage, ct);
            if (o is null) return Results.NotFound();
            var to = await db.Stages.FirstOrDefaultAsync(s => s.Id == req.StageId, ct);
            if (to is null) return Results.Problem(statusCode: 400, title: "Unknown stage");
            var from = await db.Stages.AsNoTracking().FirstAsync(s => s.Id == o.StageId, ct);
            var now = clock.UtcNow;
            try { OpportunityRules.Move(o, to, req.Reason, req.Revenue, now); } catch (IllegalTransitionException ex) { return Illegal(ex); }
            db.StageChanges.Add(new OpportunityStageChange { OpportunityId = id, FromStageId = from.Id, ToStageId = to.Id, Note = req.Note ?? req.Reason, ChangedBy = u.UserId, ChangedAt = now });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.opportunity.stage-changed", "Opportunity", id.ToString(), o.ProductId, o.Name,
                Before: new { Stage = from.Name }, After: new { Stage = to.Name, o.Status, req.Reason, o.RevenueAmount }), ct);
            return Results.Ok(OppDto(o, to));
        });

        g.MapPost("/opportunities/{id:guid}/reopen", async (Guid id, ReopenIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var o = await FindOpp(db, u, id, Permissions.SalesManage, ct);
            if (o is null) return Results.NotFound();
            var to = await db.Stages.FirstOrDefaultAsync(s => s.Id == req.StageId, ct);
            if (to is null) return Results.Problem(statusCode: 400, title: "Unknown stage");
            var from = await db.Stages.AsNoTracking().FirstAsync(s => s.Id == o.StageId, ct);
            var now = clock.UtcNow;
            try { OpportunityRules.Reopen(o, to, now); } catch (IllegalTransitionException ex) { return Illegal(ex); }
            db.StageChanges.Add(new OpportunityStageChange { OpportunityId = id, FromStageId = from.Id, ToStageId = to.Id, Note = "Reopened", ChangedBy = u.UserId, ChangedAt = now });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.opportunity.reopened", "Opportunity", id.ToString(), o.ProductId, o.Name, Before: new { Stage = from.Name }, After: new { Stage = to.Name }), ct);
            return Results.Ok(OppDto(o, to));
        });

        g.MapPost("/opportunities/{id:guid}/activities", async (Guid id, ActivityIn req, CrmDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var o = await FindOpp(db, u, id, Permissions.SalesManage, ct);
            if (o is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Summary) || req.Summary.Length > 2000) return Results.Problem(statusCode: 400, title: "A summary (max 2000 characters) is required");
            var a = new SalesActivity { OpportunityId = id, Type = req.Type, Summary = req.Summary.Trim(), DueAt = req.DueAt, OwnerUserId = u.UserId, CreatedBy = u.UserId, CreatedAt = clock.UtcNow,
                CompletedAt = req.Type is ActivityType.Task ? null : clock.UtcNow };   // calls/emails/meetings/notes are logged after the fact; tasks stay open
            db.Activities.Add(a);
            o.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/crm/activities/{a.Id}", new { a.Id, a.Type, a.CompletedAt });
        });

        g.MapPost("/activities/{id:guid}/complete", async (Guid id, CrmDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var a = await db.Activities.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            var o = await FindOpp(db, u, a.OpportunityId, Permissions.SalesManage, ct);
            if (o is null) return Results.NotFound();
            a.CompletedAt ??= clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/activities/due", async (CrmDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.SalesManage)) return Results.Forbid();
            var me = u.UserId; var now = clock.UtcNow;
            var scope = await u.AccessibleProductIdsAsync(ct);
            var q = from a in db.Activities.AsNoTracking() join o in db.Opportunities.AsNoTracking() on a.OpportunityId equals o.Id
                    where a.CompletedAt == null && a.OwnerUserId == me && (scope == null || scope.Contains(o.ProductId))
                    orderby a.DueAt select new { a.Id, a.Type, a.Summary, a.DueAt, overdue = a.DueAt != null && a.DueAt < now, opportunityId = o.Id, opportunity = o.Name };
            return Results.Ok(await q.Take(200).ToListAsync(ct));
        });
    }

    /// <summary>Default pipeline so a fresh install can take deals immediately. Idempotent: only runs when no pipeline exists.</summary>
    public static async Task SeedAsync(CrmDbContext db, CancellationToken ct)
    {
        if (await db.Pipelines.AnyAsync(ct)) return;
        var p = new Pipeline { Name = "Sales", CreatedAt = DateTimeOffset.UtcNow };
        db.Pipelines.Add(p);
        (string, int, StageKind)[] stages = [("New enquiry", 10, StageKind.Open), ("Qualified", 25, StageKind.Open), ("Demo booked", 40, StageKind.Open), ("Proposal sent", 60, StageKind.Open), ("Negotiation", 80, StageKind.Open), ("Won", 100, StageKind.Won), ("Lost", 0, StageKind.Lost)];
        for (var i = 0; i < stages.Length; i++) db.Stages.Add(new PipelineStage { PipelineId = p.Id, Name = stages[i].Item1, Order = i, Probability = stages[i].Item2, Kind = stages[i].Item3 });
        await db.SaveChangesAsync(ct);
    }
}
