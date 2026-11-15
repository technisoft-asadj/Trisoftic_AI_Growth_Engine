using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public static class LeadEndpoints
{
    public sealed record FormIn(string Name, Guid? CampaignId, string? ConsentText, Guid[]? OwnerPool, bool? Enabled);
    public sealed record SubmitIn(string? FirstName, string? LastName, string? Email, string? Phone, string? Company, string? JobTitle, string? Message, bool Consent, string? Website);
    public sealed record NewLead(Guid ContactId, Guid ProductId, Guid? CampaignId, string? Source, string? Message);
    public sealed record AssignIn(Guid? UserId);
    public sealed record StatusIn(LeadStatus Status, string? Reason, string? Note);
    public sealed record NextActionIn(string? Text, DateTimeOffset? Due);
    public sealed record RuleIn(Guid? ProductId, string Name, ScoreField Field, string? Value, int Points, bool? Enabled);

    private static object FormDto(LeadForm f) => new { f.Id, f.ProductId, f.CampaignId, f.Name, f.PublicKey, f.ConsentText, f.Enabled, ownerPool = Pool(f), submitPath = $"/api/v1/public/lead-forms/{f.PublicKey}/submit" };
    private static List<Guid> Pool(LeadForm f) => f.OwnerPool.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    private static async Task<Lead?> FindLead(CrmDbContext db, ICurrentUser u, Guid id, string permission, CancellationToken ct)
    {
        if (!u.HasPermission(permission)) return null;
        var l = await db.Leads.FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is not null && await u.CanAccessProductAsync(l.ProductId, ct) ? l : null;
    }

    private static IResult Illegal(IllegalTransitionException ex) => Results.Problem(statusCode: 409, title: ex.Message);

    public static IEndpointRouteBuilder MapLeadEndpoints(this IEndpointRouteBuilder app)
    {
        MapPublic(app);
        var g = app.MapGroup("/api/v1/crm").RequireAuthorization().WithTags("Leads");
        MapForms(g);
        MapLeads(g);
        MapRules(g);
        MapImport(g);
        return app;
    }

    // ---------- public capture (anonymous) ----------
    private static void MapPublic(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/public/lead-forms/{key}/submit", async (string key, SubmitIn body, HttpContext http, CrmDbContext db, IClock clock, IEventPublisher events, CancellationToken ct) =>
        {
            if (http.Request.ContentLength > 32 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var form = await db.LeadForms.FirstOrDefaultAsync(f => f.PublicKey == key && f.Enabled, ct);
            if (form is null) return Results.NotFound();

            // Bots fill every field; humans never see this one. Pretend success so bots learn nothing.
            if (!string.IsNullOrWhiteSpace(body.Website)) return Results.Accepted();

            foreach (var v in new[] { body.FirstName, body.LastName, body.Email, body.Phone, body.Company, body.JobTitle })
                if (v is { Length: > 300 }) return Results.Problem(statusCode: 400, title: "A field is too long");
            if (body.Message is { Length: > 4000 }) return Results.Problem(statusCode: 400, title: "The message is too long");

            var now = clock.UtcNow;
            Guid? owner = null;
            var pool = Pool(form);
            if (pool.Count > 0) { owner = pool[form.RoundRobinCursor % pool.Count]; form.RoundRobinCursor = (form.RoundRobinCursor + 1) % Math.Max(pool.Count, 1); }

            // The consent we store is the exact wording this form displayed, bound to the form and the time, never client-supplied text.
            IntakeConsent? consent = body.Consent && form.ConsentText is not null
                ? new IntakeConsent(true, $"lead form: {form.Name}", $"Ticked: \"{form.ConsentText}\" (form {form.PublicKey[..6]}…, {now:O})")
                : null;

            var res = await LeadIntake.RunAsync(db, new IntakeRow(body.FirstName, body.LastName, body.Email, body.Phone, body.Company, body.JobTitle, body.Message),
                form.ProductId, form.CampaignId, form.Id, $"form:{form.Name}", consent, owner, null, now, ct);
            if (!res.IsSuccess) return Results.Problem(statusCode: res.Error!.Kind == ErrorKind.Conflict ? 409 : 400, title: res.Error.Message);
            if (res.Value!.LeadCreated) await events.PublishAsync(new PlatformEvent("lead.created", form.ProductId, "lead", res.Value.LeadId), ct);
            return Results.Accepted();   // same answer whether or not the person already existed
        }).AllowAnonymous().RequireRateLimiting("public-forms").RequireCors("public-forms");
    }

    // ---------- forms ----------
    private static void MapForms(RouteGroupBuilder g)
    {
        g.MapGet("/products/{productId:guid}/lead-forms", async (Guid productId, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            return Results.Ok((await db.LeadForms.AsNoTracking().Where(f => f.ProductId == productId).OrderBy(f => f.Name).ToListAsync(ct)).Select(FormDto));
        });

        g.MapPost("/products/{productId:guid}/lead-forms", async (Guid productId, FormIn req, CrmDbContext db, ICurrentUser u, IUserDirectory dir, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) return Results.Problem(statusCode: 400, title: "A name is required");
            foreach (var o in req.OwnerPool ?? []) if (!await dir.IsActiveUserAsync(o, ct)) return Results.Problem(statusCode: 400, title: "The owner pool contains an unknown or inactive user");
            var f = new LeadForm
            {
                ProductId = productId, CampaignId = req.CampaignId, Name = req.Name.Trim(), ConsentText = string.IsNullOrWhiteSpace(req.ConsentText) ? null : req.ConsentText.Trim(),
                OwnerPool = string.Join(',', req.OwnerPool ?? []), PublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_'),
                Enabled = req.Enabled ?? true, CreatedBy = u.UserId, CreatedAt = clock.UtcNow,
            };
            db.LeadForms.Add(f);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.lead-form.created", "LeadForm", f.Id.ToString(), productId, f.Name, After: new { HasConsentText = f.ConsentText is not null }), ct);
            return Results.Created($"/api/v1/crm/lead-forms/{f.Id}", FormDto(f));
        });

        g.MapPut("/lead-forms/{id:guid}", async (Guid id, FormIn req, CrmDbContext db, ICurrentUser u, IUserDirectory dir, IAuditWriter audit, CancellationToken ct) =>
        {
            var f = await db.LeadForms.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null || !u.HasPermission(Permissions.LeadsManage) || !await u.CanAccessProductAsync(f.ProductId, ct)) return Results.NotFound();
            foreach (var o in req.OwnerPool ?? []) if (!await dir.IsActiveUserAsync(o, ct)) return Results.Problem(statusCode: 400, title: "The owner pool contains an unknown or inactive user");
            var consentChanged = (f.ConsentText ?? "") != (req.ConsentText ?? "").Trim();
            f.Name = req.Name.Trim(); f.CampaignId = req.CampaignId; f.ConsentText = string.IsNullOrWhiteSpace(req.ConsentText) ? null : req.ConsentText.Trim();
            f.OwnerPool = string.Join(',', req.OwnerPool ?? []); f.RoundRobinCursor = 0; if (req.Enabled is { } en) f.Enabled = en;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.lead-form.updated", "LeadForm", id.ToString(), f.ProductId, f.Name, After: new { f.Enabled, ConsentTextChanged = consentChanged }), ct);
            return Results.Ok(FormDto(f));
        });
    }

    // ---------- leads ----------
    private static void MapLeads(RouteGroupBuilder g)
    {
        g.MapGet("/leads", async (CrmDbContext db, ICurrentUser u, LeadStatus? status, Guid? productId, Guid? ownerId, bool? overdue, bool? unassigned, IClock clock, string? cursor, int? take, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var q = db.Leads.AsNoTracking().AsQueryable();
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(l => scope.Contains(l.ProductId));
            if (status is not null) q = q.Where(l => l.Status == status);
            if (productId is not null) q = q.Where(l => l.ProductId == productId);
            if (ownerId is not null) q = q.Where(l => l.OwnerUserId == ownerId);
            if (unassigned == true) q = q.Where(l => l.OwnerUserId == null);
            if (overdue == true) { var now = clock.UtcNow; q = q.Where(l => l.NextActionDue != null && l.NextActionDue < now && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified)); }
            if (Guid.TryParse(cursor, out var cur)) q = q.Where(l => l.Id.CompareTo(cur) < 0);
            var n = Math.Clamp(take ?? 50, 1, 200);
            var rows = await q.OrderByDescending(l => l.Id).Take(n + 1).ToListAsync(ct);
            var cids = rows.Take(n).Select(l => l.ContactId).ToList();
            var names = await db.Contacts.AsNoTracking().Where(c => cids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
            return Results.Ok(new
            {
                items = rows.Take(n).Select(l => new { l.Id, l.ContactId, contactName = names.GetValueOrDefault(l.ContactId), l.ProductId, l.CampaignId, l.Status, l.Source, l.Score, l.OwnerUserId, l.NextAction, l.NextActionDue, l.CreatedAt }),
                nextCursor = rows.Count > n ? rows[n - 1].Id.ToString() : null,
            });
        });

        g.MapGet("/leads/{id:guid}", async (Guid id, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var l = await FindLead(db, u, id, Permissions.CrmRead, ct);
            if (l is null) return Results.NotFound();
            var contact = await db.Contacts.AsNoTracking().FirstAsync(c => c.Id == l.ContactId, ct);
            var (score, hits) = await LeadIntake.ScoreAsync(db, l, contact, ct);
            return Results.Ok(new { lead = l, contact = new { contact.Id, contact.DisplayName, contact.JobTitle, contact.CompanyId }, scoreBreakdown = hits, currentRuleScore = score });
        });

        g.MapPost("/leads", async (NewLead req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage) || !await u.CanAccessProductAsync(req.ProductId, ct)) return Results.NotFound();
            var contact = await (await CrmEndpoints.VisibleAsync(db, u, false, ct)).FirstOrDefaultAsync(c => c.Id == req.ContactId, ct);
            if (contact is null) return Results.Problem(statusCode: 400, title: "Contact not found");
            if (await db.Leads.AnyAsync(l => l.ContactId == req.ContactId && l.ProductId == req.ProductId && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified), ct))
                return Results.Problem(statusCode: 409, title: "This contact already has an open lead for this product");
            var now = clock.UtcNow;
            var lead = new Lead { ContactId = contact.Id, ProductId = req.ProductId, CampaignId = req.CampaignId, Source = string.IsNullOrWhiteSpace(req.Source) ? "manual" : req.Source.Trim(), Message = req.Message, OwnerUserId = u.UserId, CreatedAt = now, UpdatedAt = now };
            lead.Score = (await LeadIntake.ScoreAsync(db, lead, contact, ct)).Score;
            db.Leads.Add(lead);
            if (!await db.Associations.AnyAsync(a => a.ContactId == contact.Id && a.ProductId == req.ProductId && a.CampaignId == req.CampaignId, ct))
                db.Associations.Add(new ContactAssociation { ContactId = contact.Id, ProductId = req.ProductId, CampaignId = req.CampaignId });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.lead.created", "Lead", lead.Id.ToString(), lead.ProductId, contact.DisplayName, After: new { lead.Source, lead.Score }), ct);
            await events.LeadAsync("lead.created", lead, ct);
            return Results.Created($"/api/v1/crm/leads/{lead.Id}", new { lead.Id, lead.Score });
        });

        g.MapPut("/leads/{id:guid}/assign", async (Guid id, AssignIn req, CrmDbContext db, ICurrentUser u, IUserDirectory dir, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            var l = await FindLead(db, u, id, Permissions.LeadsManage, ct);
            if (l is null) return Results.NotFound();
            if (req.UserId is { } uid && !await dir.IsActiveUserAsync(uid, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inactive user");
            var before = l.OwnerUserId;
            l.OwnerUserId = req.UserId; l.UpdatedAt = clock.UtcNow;
            db.Interactions.Add(new Interaction { ContactId = l.ContactId, Type = InteractionType.System, ProductId = l.ProductId, Summary = req.UserId is null ? "Lead unassigned" : "Lead assigned", OccurredAt = clock.UtcNow, CreatedBy = u.UserId });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.lead.assigned", "Lead", id.ToString(), l.ProductId, Before: new { Owner = before }, After: new { Owner = req.UserId }), ct);
            if (req.UserId is not null) await events.LeadAsync("lead.assigned", l, ct);
            return Results.NoContent();
        });

        g.MapPost("/leads/{id:guid}/status", async (Guid id, StatusIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            var l = await FindLead(db, u, id, Permissions.LeadsManage, ct);
            if (l is null) return Results.NotFound();
            var before = l.Status; var now = clock.UtcNow;
            try
            {
                switch (req.Status)
                {
                    case LeadStatus.Contacted: l.MarkContacted(now); break;
                    case LeadStatus.Qualified: l.Qualify(req.Note ?? "", now); break;
                    case LeadStatus.Disqualified: l.Disqualify(req.Reason ?? "", now); break;
                    case LeadStatus.New: l.Reopen(); break;
                    default: return Results.Problem(statusCode: 400, title: "Leads become Converted by creating an opportunity from them");
                }
            }
            catch (IllegalTransitionException ex) { return Illegal(ex); }
            l.UpdatedAt = now;
            db.Interactions.Add(new Interaction { ContactId = l.ContactId, Type = InteractionType.System, ProductId = l.ProductId, Summary = $"Lead {before} -> {l.Status}" + (string.IsNullOrWhiteSpace(req.Reason) ? "" : $" ({req.Reason})"), OccurredAt = now, CreatedBy = u.UserId });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.lead.status-changed", "Lead", id.ToString(), l.ProductId, Before: new { Status = before }, After: new { l.Status, req.Reason, req.Note }), ct);
            await events.LeadAsync("lead.status-changed", l, ct, previousStatus: before.ToString());
            return Results.Ok(new { l.Id, l.Status });
        });

        g.MapPut("/leads/{id:guid}/next-action", async (Guid id, NextActionIn req, CrmDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var l = await FindLead(db, u, id, Permissions.LeadsManage, ct);
            if (l is null) return Results.NotFound();
            if (!l.IsOpen) return Results.Problem(statusCode: 409, title: $"A {l.Status} lead has no next action");
            if (req.Text is { Length: > 500 }) return Results.Problem(statusCode: 400, title: "The next action is too long");
            l.NextAction = string.IsNullOrWhiteSpace(req.Text) ? null : req.Text.Trim(); l.NextActionDue = req.Due; l.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/leads/{id:guid}/rescore", async (Guid id, CrmDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var l = await FindLead(db, u, id, Permissions.LeadsManage, ct);
            if (l is null) return Results.NotFound();
            var contact = await db.Contacts.FirstAsync(c => c.Id == l.ContactId, ct);
            var (score, hits) = await LeadIntake.ScoreAsync(db, l, contact, ct);
            l.Score = score; l.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { l.Score, hits });
        });
    }

    // ---------- scoring rules ----------
    private static void MapRules(RouteGroupBuilder g)
    {
        static string? Validate(RuleIn r)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) return "A rule name is required";
            if (r.Points is < -50 or > 50) return "Points must be between -50 and 50";
            var needsValue = r.Field is ScoreField.SourceEquals or ScoreField.JobTitleContains or ScoreField.TagEquals or ScoreField.MessageContains;
            if (needsValue && string.IsNullOrWhiteSpace(r.Value)) return $"{r.Field} needs a value";
            return null;
        }

        g.MapGet("/score-rules", async (CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
            u.HasPermission(Permissions.CrmRead) ? Results.Ok(await db.ScoreRules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct)) : Results.Forbid());

        g.MapPost("/score-rules", async (RuleIn req, CrmDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage)) return Results.Forbid();
            if (Validate(req) is { } err) return Results.Problem(statusCode: 400, title: err);
            if (req.ProductId is { } pid && !await u.CanAccessProductAsync(pid, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inaccessible product");
            var r = new ScoreRule { ProductId = req.ProductId, Name = req.Name.Trim(), Field = req.Field, Value = req.Value?.Trim(), Points = req.Points, Enabled = req.Enabled ?? true };
            db.ScoreRules.Add(r);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.score-rule.created", "ScoreRule", r.Id.ToString(), r.ProductId, r.Name, After: new { r.Field, r.Value, r.Points }), ct);
            return Results.Created($"/api/v1/crm/score-rules/{r.Id}", r);
        });

        g.MapPut("/score-rules/{id:guid}", async (Guid id, RuleIn req, CrmDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage)) return Results.Forbid();
            var r = await db.ScoreRules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Results.NotFound();
            if (Validate(req) is { } err) return Results.Problem(statusCode: 400, title: err);
            r.Name = req.Name.Trim(); r.Field = req.Field; r.Value = req.Value?.Trim(); r.Points = req.Points; if (req.Enabled is { } en) r.Enabled = en;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.score-rule.updated", "ScoreRule", id.ToString(), r.ProductId, r.Name), ct);
            return Results.Ok(r);
        });

        g.MapDelete("/score-rules/{id:guid}", async (Guid id, CrmDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage)) return Results.Forbid();
            var r = await db.ScoreRules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Results.NotFound();
            db.ScoreRules.Remove(r);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.score-rule.deleted", "ScoreRule", id.ToString(), r.ProductId, r.Name), ct);
            return Results.NoContent();
        });
    }

    // ---------- CSV import ----------
    private static void MapImport(RouteGroupBuilder g)
    {
        g.MapPost("/products/{productId:guid}/leads/import", async (Guid productId, bool? dryRun, HttpRequest req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, IEventPublisher events, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            if (!req.HasFormContentType) return Results.Problem(statusCode: 400, title: "Expected multipart/form-data with a CSV file");
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.Problem(statusCode: 400, title: "A CSV file is required");
            if (file.Length > 2 * 1024 * 1024) return Results.Problem(statusCode: 400, title: "The file is larger than 2 MB");
            if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return Results.Problem(statusCode: 400, title: "Only .csv files are accepted");

            string text; using (var sr = new StreamReader(file.OpenReadStream(), Encoding.UTF8)) text = await sr.ReadToEndAsync(ct);
            var rows = Csv.Parse(text);
            if (rows.Count < 2) return Results.Problem(statusCode: 400, title: "The file needs a header row and at least one data row");
            if (rows.Count - 1 > 5000) return Results.Problem(statusCode: 400, title: "At most 5000 rows per import");

            var header = new Dictionary<string, int>();
            for (var i = 0; i < rows[0].Length; i++) header.TryAdd(rows[0][i].Trim().ToLowerInvariant().Replace("_", "").Replace(" ", ""), i);   // first column wins on duplicates
            if (!header.ContainsKey("email") && !header.ContainsKey("phone")) return Results.Problem(statusCode: 400, title: "The file needs an email or phone column");
            string? Cell(string[] r, string name) => header.TryGetValue(name, out var i) && i < r.Length && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;

            var preview = dryRun == true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var newLeadIds = new List<Guid>();
            var errors = new List<object>(); int created = 0, existing = 0, leads = 0, attached = 0, consentRows = 0;
            var now = clock.UtcNow;
            for (var i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                var consentRaw = Cell(r, "consent");
                var claimsConsent = consentRaw is not null && consentRaw.ToLowerInvariant() is "yes" or "y" or "true" or "1";
                var consent = claimsConsent ? new IntakeConsent(true, "csv import", Cell(r, "consentevidence")) : null;
                var res = await LeadIntake.RunAsync(db, new IntakeRow(Cell(r, "firstname"), Cell(r, "lastname"), Cell(r, "email"), Cell(r, "phone"), Cell(r, "company"), Cell(r, "jobtitle"), Cell(r, "message")),
                    productId, null, null, Cell(r, "source") is { } s ? $"import:{s}" : "import", consent, u.UserId, u.UserId, now, ct);
                if (!res.IsSuccess) { errors.Add(new { row = i + 1, message = res.Error!.Message }); db.ChangeTracker.Clear(); continue; }
                if (res.Value!.ContactCreated) created++; else existing++;
                if (res.Value.LeadCreated) { leads++; newLeadIds.Add(res.Value.LeadId); } else attached++;
                if (claimsConsent) consentRows++;
            }
            if (preview) await tx.RollbackAsync(ct); else await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            if (!preview) foreach (var leadId in newLeadIds) await events.PublishAsync(new PlatformEvent("lead.created", productId, "lead", leadId), ct);
            if (!preview) await audit.WriteAsync(new AuditEntry("crm.leads.imported", "Lead", null, productId, file.FileName, After: new { total = rows.Count - 1, created, existing, leads, attached, errors = errors.Count, consentRows }), ct);
            return Results.Ok(new { dryRun = preview, total = rows.Count - 1, contactsCreated = created, contactsMatched = existing, leadsCreated = leads, attachedToOpenLead = attached, rowsWithConsent = consentRows, errors });
        }).DisableAntiforgery();
    }
}
