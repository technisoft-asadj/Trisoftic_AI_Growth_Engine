using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Content;

public static class ContentModule
{
    public static readonly string[] Formats = ["Post", "Carousel", "Script", "Storyboard", "CreativeBrief", "ImagePrompt", "DemoScript", "Blog", "SeoDraft", "LandingCopy", "AdVariant", "Email"];

    public static IServiceCollection AddContentModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<ContentDbContext>(c, ContentDbContext.Schema);
        s.AddScoped<IApprovalSubjectHandler, ContentApprovalHandler>();
        s.AddScoped<IContentPublishSource, ContentPublishSource>();
        return s;
    }

    public sealed record CampaignRequest(string Name, string? Objective, string? Audience, string? Market, string? Channels, string? DestinationUrl, string? TrackingNotes,
        decimal? BudgetUsd, DateOnly? StartDate, DateOnly? EndDate, string? SuccessThresholds);
    public sealed record CreateContentRequest(string Title, string Channel, string? Format, string? Language, string Body, Guid? CampaignId, Guid? SourceDraftId);
    public sealed record SaveVersionRequest(string? Title, string Body, string? Note, int BaseVersion);
    public sealed record ScheduleRequest(DateTimeOffset At);

    private static object Dto(ContentItem i) => new { i.Id, i.ProductId, i.CampaignId, i.Title, i.Channel, i.Format, i.Language, i.CurrentVersion, i.ApprovedVersion, i.Status, i.ScheduledFor, i.UpdatedAt };

    private static async Task<ContentItem?> Load(ContentDbContext db, ICurrentUser u, Guid id, string? write, CancellationToken ct)
    {
        var i = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null || !u.HasPermission(write ?? Permissions.ProductsRead) || !await u.CanAccessProductAsync(i.ProductId, ct)) return null;
        return i;
    }

    private static IResult Illegal(IllegalTransitionException ex) => Results.Problem(statusCode: 409, title: ex.Message);

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Content");
        MapCampaigns(g);

        g.MapGet("/content-formats", () => Results.Ok(Formats));

        // ---- queue / calendar across the products the caller can see ----
        g.MapGet("/content", async (ContentDbContext db, ICurrentUser u, ContentStatus? status, Guid? productId, Guid? campaignId, int? take, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead)) return Results.Forbid();
            var q = db.Items.AsNoTracking().AsQueryable();
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(i => scope.Contains(i.ProductId));
            if (status is not null) q = q.Where(i => i.Status == status);
            if (productId is not null) q = q.Where(i => i.ProductId == productId);
            if (campaignId is not null) q = q.Where(i => i.CampaignId == campaignId);
            return Results.Ok((await q.OrderByDescending(i => i.UpdatedAt).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct)).Select(Dto));
        });

        g.MapGet("/calendar", async (ContentDbContext db, ICurrentUser u, DateTimeOffset from, DateTimeOffset to, Guid? productId, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead)) return Results.Forbid();
            if (to <= from || to - from > TimeSpan.FromDays(93)) return Results.Problem(statusCode: 400, title: "Provide a range of up to 93 days");
            var q = db.Items.AsNoTracking().Where(i => i.Status == ContentStatus.Scheduled && i.ScheduledFor >= from && i.ScheduledFor < to);
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(i => scope.Contains(i.ProductId));
            if (productId is not null) q = q.Where(i => i.ProductId == productId);
            return Results.Ok((await q.OrderBy(i => i.ScheduledFor).ToListAsync(ct)).Select(Dto));
        });

        g.MapPost("/products/{productId:guid}/content", async (Guid productId, CreateContentRequest req, ContentDbContext db, ICurrentUser u, IAiDraftLookup drafts, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ContentWrite) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 300) return Results.Problem(statusCode: 400, title: "A title (max 300 characters) is required");
            if (string.IsNullOrWhiteSpace(req.Channel) || req.Channel.Length > 60) return Results.Problem(statusCode: 400, title: "A channel is required");
            var format = req.Format ?? "Post";
            if (!Formats.Contains(format)) return Results.Problem(statusCode: 400, title: "Unknown format");
            var body = req.Body ?? "";
            if (body.Length > 60000) return Results.Problem(statusCode: 400, title: "The body is too long");
            if (req.CampaignId is { } cid && !await db.Campaigns.AnyAsync(c => c.Id == cid && c.ProductId == productId, ct)) return Results.Problem(statusCode: 400, title: "Campaign not found for this product");

            Guid? source = null;
            if (req.SourceDraftId is { } did)
            {
                var d = await drafts.GetAsync(did, productId, ct);
                if (d is null) return Results.Problem(statusCode: 400, title: "The AI draft was not found for this product");
                if (string.IsNullOrWhiteSpace(body)) body = d.Body;
                source = d.Id;
            }

            var now = clock.UtcNow;
            var item = new ContentItem { ProductId = productId, CampaignId = req.CampaignId, Title = req.Title.Trim(), Channel = req.Channel.Trim(), Format = format, Language = req.Language, CurrentVersion = 1, CreatedBy = u.UserId, CreatedAt = now, UpdatedAt = now };
            db.Items.Add(item);
            db.Versions.Add(new ContentVersion { ContentItemId = item.Id, Number = 1, Title = item.Title, Body = body, Note = source is null ? "Created" : "Created from AI draft", SourceDraftId = source, CreatedBy = u.UserId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("content.created", "ContentItem", item.Id.ToString(), productId, item.Title, After: new { item.Channel, item.Format, SourceDraftId = source }), ct);
            return Results.Created($"/api/v1/content/{item.Id}", Dto(item));
        });

        g.MapGet("/content/{id:guid}", async (Guid id, ContentDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var i = await Load(db, u, id, null, ct);
            if (i is null) return Results.NotFound();
            var versions = await db.Versions.AsNoTracking().Where(v => v.ContentItemId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
            var cur = versions.First(v => v.Number == i.CurrentVersion);
            return Results.Ok(new
            {
                item = Dto(i), current = new { cur.Number, cur.Title, cur.Body, cur.Note, cur.SourceDraftId },
                versions = versions.Select(v => new { v.Number, v.Title, v.Note, v.SourceDraftId, v.CreatedBy, v.CreatedAt, approved = i.ApprovedVersion == v.Number }),
            });
        });

        g.MapGet("/content/{id:guid}/versions/{n:int}", async (Guid id, int n, ContentDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (await Load(db, u, id, null, ct) is null) return Results.NotFound();
            var v = await db.Versions.AsNoTracking().FirstOrDefaultAsync(x => x.ContentItemId == id && x.Number == n, ct);
            return v is null ? Results.NotFound() : Results.Ok(new { v.Number, v.Title, v.Body, v.Note, v.SourceDraftId, v.CreatedAt });
        });

        g.MapPost("/content/{id:guid}/versions", async (Guid id, SaveVersionRequest req, ContentDbContext db, ICurrentUser u, IApprovalService approvals, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var i = await Load(db, u, id, Permissions.ContentWrite, ct);
            if (i is null) return Results.NotFound();
            if (req.BaseVersion != i.CurrentVersion) return Results.Problem(statusCode: 409, title: $"Someone saved v{i.CurrentVersion} since you opened v{req.BaseVersion}. Reload and merge your changes.");
            if ((req.Body ?? "").Length > 60000) return Results.Problem(statusCode: 400, title: "The body is too long");
            if (i.Status == ContentStatus.Cancelled) return Results.Problem(statusCode: 409, title: "Cancelled content cannot be edited");
            if (i.Status == ContentStatus.Publishing) return Results.Problem(statusCode: 409, title: "This content is being published right now; edit it once publishing finishes");

            var wasInReview = i.Status == ContentStatus.InReview;
            var wasScheduled = i.Status == ContentStatus.Scheduled;
            var next = i.CurrentVersion + 1;
            i.AddVersion(next); i.UpdatedAt = clock.UtcNow;
            if (!string.IsNullOrWhiteSpace(req.Title)) i.Title = req.Title.Trim();
            db.Versions.Add(new ContentVersion { ContentItemId = id, Number = next, Title = i.Title, Body = req.Body ?? "", Note = req.Note, CreatedBy = u.UserId, CreatedAt = clock.UtcNow });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "Concurrent save detected. Reload and try again."); }
            if (wasInReview) await approvals.WithdrawAsync(ContentApprovalHandler.Subject, id.ToString(), ct);   // the pending request was for the old version
            await audit.WriteAsync(new AuditEntry("content.version-saved", "ContentItem", id.ToString(), i.ProductId, $"{i.Title} v{next}",
                After: new { req.Note, DroppedSchedule = wasScheduled, WithdrewReview = wasInReview }), ct);
            return Results.Ok(Dto(i));
        });

        g.MapPost("/content/{id:guid}/submit", async (Guid id, ContentDbContext db, ICurrentUser u, IApprovalService approvals, IClock clock, CancellationToken ct) =>
        {
            var i = await Load(db, u, id, Permissions.ContentWrite, ct);
            if (i is null) return Results.NotFound();
            var body = await db.Versions.AsNoTracking().Where(v => v.ContentItemId == id && v.Number == i.CurrentVersion).Select(v => v.Body).FirstAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return Results.Problem(statusCode: 400, title: "Add some content before submitting for approval");
            try { i.Submit(); } catch (IllegalTransitionException ex) { return Illegal(ex); }
            i.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            var r = await approvals.RequestAsync(new ApprovalRequest(ContentApprovalHandler.Subject, id.ToString(), i.CurrentVersion, i.ProductId, $"{i.Title} v{i.CurrentVersion} ({i.Channel})", $"{i.Format} for {i.Channel}"), ct);
            if (!r.IsSuccess) { i.Withdraw(); await db.SaveChangesAsync(ct); return Results.Problem(statusCode: 409, title: r.Error!.Message); }
            return Results.Ok(Dto(i));
        });

        g.MapPost("/content/{id:guid}/withdraw", async (Guid id, ContentDbContext db, ICurrentUser u, IApprovalService approvals, IClock clock, CancellationToken ct) =>
        {
            var i = await Load(db, u, id, Permissions.ContentWrite, ct);
            if (i is null) return Results.NotFound();
            try { i.Withdraw(); } catch (IllegalTransitionException ex) { return Illegal(ex); }
            i.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await approvals.WithdrawAsync(ContentApprovalHandler.Subject, id.ToString(), ct);
            return Results.Ok(Dto(i));
        });

        g.MapPost("/content/{id:guid}/schedule", async (Guid id, ScheduleRequest req, ContentDbContext db, ICurrentUser u, IPauseSwitch pause, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var i = await Load(db, u, id, Permissions.PublishingManage, ct);
            if (i is null) return Results.NotFound();
            try { i.Schedule(req.At, clock.UtcNow); } catch (IllegalTransitionException ex) { return Illegal(ex); }
            i.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("content.scheduled", "ContentItem", id.ToString(), i.ProductId, $"{i.Title} v{i.CurrentVersion}", After: new { req.At, i.Channel }), ct);
            var paused = await pause.IsPausedAsync(PauseScope.Publishing, ct);
            return Results.Ok(new { item = Dto(i), publishingPaused = paused });
        });

        g.MapPost("/content/{id:guid}/unschedule", async (Guid id, ContentDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var i = await Load(db, u, id, Permissions.PublishingManage, ct);
            if (i is null) return Results.NotFound();
            try { i.Unschedule(); } catch (IllegalTransitionException ex) { return Illegal(ex); }
            i.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("content.unscheduled", "ContentItem", id.ToString(), i.ProductId, i.Title), ct);
            return Results.Ok(Dto(i));
        });

        g.MapPost("/content/{id:guid}/cancel", async (Guid id, ContentDbContext db, ICurrentUser u, IApprovalService approvals, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var i = await Load(db, u, id, Permissions.ContentWrite, ct);
            if (i is null) return Results.NotFound();
            var wasInReview = i.Status == ContentStatus.InReview;
            try { i.Cancel(); } catch (IllegalTransitionException ex) { return Illegal(ex); }
            i.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            if (wasInReview) await approvals.WithdrawAsync(ContentApprovalHandler.Subject, id.ToString(), ct);
            await audit.WriteAsync(new AuditEntry("content.cancelled", "ContentItem", id.ToString(), i.ProductId, i.Title), ct);
            return Results.Ok(Dto(i));
        });

        return app;
    }

    private static void MapCampaigns(RouteGroupBuilder g)
    {
        static List<string> Validate(CampaignRequest r)
        {
            var e = new List<string>();
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) e.Add("A name (max 200 characters) is required.");
            if (r.BudgetUsd is < 0) e.Add("Budget cannot be negative.");
            if (r.StartDate is { } s && r.EndDate is { } en && en < s) e.Add("The end date is before the start date.");
            if (r.DestinationUrl is { Length: > 0 } && (!Uri.TryCreate(r.DestinationUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))) e.Add("The destination must be an http(s) URL.");
            return e;
        }
        static void Apply(Campaign c, CampaignRequest r)
        {
            c.Name = r.Name.Trim(); c.Objective = r.Objective ?? ""; c.Audience = r.Audience ?? ""; c.Market = r.Market ?? ""; c.Channels = r.Channels ?? "";
            c.DestinationUrl = r.DestinationUrl; c.TrackingNotes = r.TrackingNotes; c.BudgetUsd = r.BudgetUsd; c.StartDate = r.StartDate; c.EndDate = r.EndDate; c.SuccessThresholds = r.SuccessThresholds;
        }

        g.MapGet("/products/{productId:guid}/campaigns", async (Guid productId, ContentDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            return Results.Ok(await db.Campaigns.AsNoTracking().Where(c => c.ProductId == productId).OrderByDescending(c => c.UpdatedAt).ToListAsync(ct));
        });

        g.MapGet("/campaigns/{id:guid}", async (Guid id, ContentDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return c is null || !u.HasPermission(Permissions.ProductsRead) || !await u.CanAccessProductAsync(c.ProductId, ct) ? Results.NotFound() : Results.Ok(c);
        });

        g.MapPost("/products/{productId:guid}/campaigns", async (Guid productId, CampaignRequest req, ContentDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var errors = Validate(req);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["campaign"] = [.. errors] });
            var now = clock.UtcNow;
            var c = new Campaign { ProductId = productId, CreatedBy = u.UserId, CreatedAt = now, UpdatedAt = now };
            Apply(c, req);
            db.Campaigns.Add(c);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("campaign.created", "Campaign", c.Id.ToString(), productId, c.Name), ct);
            return Results.Created($"/api/v1/campaigns/{c.Id}", c);
        });

        g.MapPut("/campaigns/{id:guid}", async (Guid id, CampaignRequest req, ContentDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null || !u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(c.ProductId, ct)) return Results.NotFound();
            var errors = Validate(req);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["campaign"] = [.. errors] });
            Apply(c, req); c.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("campaign.updated", "Campaign", id.ToString(), c.ProductId, c.Name), ct);
            return Results.Ok(c);
        });

        g.MapPost("/campaigns/{id:guid}/status/{status}", async (Guid id, string status, ContentDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null || !u.HasPermission(Permissions.CampaignsManage) || !await u.CanAccessProductAsync(c.ProductId, ct)) return Results.NotFound();
            if (!Enum.TryParse<CampaignStatus>(status, true, out var s)) return Results.Problem(statusCode: 400, title: "Unknown status");
            if (c.Status is CampaignStatus.Completed or CampaignStatus.Cancelled) return Results.Problem(statusCode: 409, title: $"A {c.Status} campaign cannot change status");
            var before = c.Status; c.Status = s; c.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("campaign.status-changed", "Campaign", id.ToString(), c.ProductId, c.Name, Before: new { before }, After: new { s }), ct);
            return Results.Ok(c);
        });
    }
}
