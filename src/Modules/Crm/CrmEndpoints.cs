using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public static class CrmEndpoints
{
    public sealed record MethodIn(MethodType Type, string Value, bool IsPrimary);
    public sealed record ConsentIn(ContactChannel Channel, ContactPurpose Purpose, ConsentStatus Status, string Source, string? Evidence);
    public sealed record AssocIn(Guid ProductId, Guid? CampaignId);
    public sealed record CreateContact(string? FirstName, string? LastName, string? JobTitle, Guid? CompanyId, string? Source, string? Notes,
        MethodIn[]? Methods, string[]? Tags, AssocIn[]? Associations, ConsentIn[]? Consents);
    public sealed record UpdateContact(string? FirstName, string? LastName, string? JobTitle, Guid? CompanyId, Guid? OwnerUserId, string? Source, string? Notes);
    public sealed record CompanyIn(string Name, string? Website, string? Industry, string? Notes);
    public sealed record InteractionIn(InteractionType Type, string Summary, Guid? ProductId);
    public sealed record SuppressIn(ContactChannel Channel, string Value, string Reason);
    public sealed record MergeIn(Guid SourceId);

    // ---------- visibility ----------

    /// <summary>
    /// Contacts the caller may see. Unrestricted users see all. Product-scoped users see contacts associated with one of their products,
    /// plus unassociated contacts they created themselves. Merged tombstones are never listed.
    /// </summary>
    public static async Task<IQueryable<Contact>> VisibleAsync(CrmDbContext db, ICurrentUser u, bool includeMerged, CancellationToken ct)
    {
        var q = db.Contacts.AsQueryable();
        if (!includeMerged) q = q.Where(c => c.MergedIntoId == null);
        var scope = await u.AccessibleProductIdsAsync(ct);
        if (scope is null) return q;
        var me = u.UserId;
        return q.Where(c => db.Associations.Any(a => a.ContactId == c.Id && scope.Contains(a.ProductId))
                         || (!db.Associations.Any(a => a.ContactId == c.Id) && c.CreatedBy == me));
    }

    private static async Task<Contact?> Find(CrmDbContext db, ICurrentUser u, Guid id, string permission, CancellationToken ct, bool includeMerged = false)
    {
        if (!u.HasPermission(permission)) return null;
        return await (await VisibleAsync(db, u, includeMerged, ct)).FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    private static bool IsUnique(DbUpdateException e) => e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public static IEndpointRouteBuilder MapCrmEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/crm").RequireAuthorization().WithTags("CRM");
        MapCompanies(g);
        MapContacts(g);
        MapConsent(g);
        MapMerge(g);
        return app;
    }

    // ---------- companies ----------
    private static void MapCompanies(RouteGroupBuilder g)
    {
        g.MapGet("/companies", async (CrmDbContext db, ICurrentUser u, string? q, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var query = db.Companies.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => EF.Functions.ILike(c.Name, $"%{q}%") || (c.Domain != null && EF.Functions.ILike(c.Domain, $"%{q}%")));
            return Results.Ok(await query.OrderBy(c => c.Name).Take(200).ToListAsync(ct));
        });

        g.MapPost("/companies", async (CompanyIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmWrite)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 300) return Results.Problem(statusCode: 400, title: "A company name is required");
            var domain = Normalizer.Domain(req.Website);
            if (req.Website is { Length: > 0 } && domain is null) return Results.Problem(statusCode: 400, title: "The website is not a valid address");
            if (domain is not null && await db.Companies.AnyAsync(c => c.Domain == domain, ct)) return Results.Problem(statusCode: 409, title: $"A company with the domain {domain} already exists");
            var now = clock.UtcNow;
            var c = new Company { Name = req.Name.Trim(), Website = req.Website, Domain = domain, Industry = req.Industry, Notes = req.Notes, CreatedBy = u.UserId, CreatedAt = now, UpdatedAt = now };
            db.Companies.Add(c);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.company.created", "Company", c.Id.ToString(), Summary: c.Name), ct);
            return Results.Created($"/api/v1/crm/companies/{c.Id}", c);
        });

        g.MapPut("/companies/{id:guid}", async (Guid id, CompanyIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmWrite)) return Results.Forbid();
            var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            var domain = Normalizer.Domain(req.Website);
            if (domain is not null && await db.Companies.AnyAsync(x => x.Domain == domain && x.Id != id, ct)) return Results.Problem(statusCode: 409, title: $"Another company already uses {domain}");
            c.Name = req.Name.Trim(); c.Website = req.Website; c.Domain = domain; c.Industry = req.Industry; c.Notes = req.Notes; c.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.company.updated", "Company", id.ToString(), Summary: c.Name), ct);
            return Results.Ok(c);
        });
    }

    // ---------- contacts ----------
    private static void MapContacts(RouteGroupBuilder g)
    {
        g.MapGet("/contacts", async (CrmDbContext db, ICurrentUser u, string? q, string? tag, Guid? productId, Guid? campaignId, Guid? companyId, string? source, Guid? ownerId,
            ContactChannel? consentChannel, string? cursor, int? take, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmRead)) return Results.Forbid();
            var query = (await VisibleAsync(db, u, false, ct)).AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(c => EF.Functions.ILike(c.DisplayName, like)
                    || db.Methods.Any(m => m.ContactId == c.Id && EF.Functions.ILike(m.NormalizedValue, like))
                    || (c.CompanyId != null && db.Companies.Any(co => co.Id == c.CompanyId && EF.Functions.ILike(co.Name, like))));
            }
            if (!string.IsNullOrWhiteSpace(tag)) { var t = tag.Trim().ToLowerInvariant(); query = query.Where(c => db.ContactTags.Any(ct2 => ct2.ContactId == c.Id && db.Tags.Any(x => x.Id == ct2.TagId && x.Name == t))); }
            if (productId is not null) query = query.Where(c => db.Associations.Any(a => a.ContactId == c.Id && a.ProductId == productId));
            if (campaignId is not null) query = query.Where(c => db.Associations.Any(a => a.ContactId == c.Id && a.CampaignId == campaignId));
            if (companyId is not null) query = query.Where(c => c.CompanyId == companyId);
            if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.Source == source);
            if (ownerId is not null) query = query.Where(c => c.OwnerUserId == ownerId);
            if (consentChannel is { } ch)   // segment: people whose latest marketing consent on this channel is Granted
                query = query.Where(c => db.Consents.Where(k => k.ContactId == c.Id && k.Channel == ch && k.Purpose == ContactPurpose.Marketing)
                    .OrderByDescending(k => k.RecordedAt).ThenByDescending(k => k.Id).Select(k => k.Status).FirstOrDefault() == ConsentStatus.Granted);
            if (Guid.TryParse(cursor, out var cur)) query = query.Where(c => c.Id.CompareTo(cur) < 0);
            var n = Math.Clamp(take ?? 50, 1, 200);
            var rows = await query.OrderByDescending(c => c.Id).Take(n + 1).ToListAsync(ct);
            var ids = rows.Take(n).Select(r => r.Id).ToList();
            var primary = await db.Methods.AsNoTracking().Where(m => ids.Contains(m.ContactId)).OrderByDescending(m => m.IsPrimary).ToListAsync(ct);
            return Results.Ok(new
            {
                items = rows.Take(n).Select(c => new { c.Id, c.DisplayName, c.JobTitle, c.CompanyId, c.Source, c.OwnerUserId, methods = primary.Where(m => m.ContactId == c.Id).Select(m => new { m.Type, m.Value }) }),
                nextCursor = rows.Count > n ? rows[n - 1].Id.ToString() : null,
            });
        });

        g.MapPost("/contacts", async (CreateContact req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmWrite)) return Results.Forbid();
            var first = (req.FirstName ?? "").Trim(); var last = (req.LastName ?? "").Trim();
            var methods = new List<(MethodType Type, string Value, string Norm, bool Primary)>();
            foreach (var m in req.Methods ?? [])
            {
                var norm = Normalizer.Method(m.Type, m.Value);
                if (norm is null) return Results.Problem(statusCode: 400, title: $"'{m.Value}' is not a valid {m.Type}");
                if (!methods.Any(x => x.Type == m.Type && x.Norm == norm)) methods.Add((m.Type, m.Value.Trim(), norm, m.IsPrimary));
            }
            if (first.Length == 0 && last.Length == 0 && methods.Count == 0) return Results.Problem(statusCode: 400, title: "Provide a name or at least one email/phone");

            foreach (var (type, _, norm, _) in methods)
            {
                var existing = await db.Methods.AsNoTracking().Where(x => x.Type == type && x.NormalizedValue == norm).Select(x => (Guid?)x.ContactId).FirstOrDefaultAsync(ct);
                if (existing is not null) return Results.Problem(statusCode: 409, title: "A contact with this email/phone already exists", extensions: new Dictionary<string, object?> { ["existingContactId"] = existing });
            }
            foreach (var a in req.Associations ?? []) if (!await u.CanAccessProductAsync(a.ProductId, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inaccessible product");
            if (req.CompanyId is { } coid && !await db.Companies.AnyAsync(c => c.Id == coid, ct)) return Results.Problem(statusCode: 400, title: "Company not found");
            foreach (var k in req.Consents ?? []) if (ConsentError(k) is { } ce) return Results.Problem(statusCode: 400, title: ce);

            var now = clock.UtcNow;
            var c = new Contact
            {
                FirstName = first, LastName = last, DisplayName = Normalizer.DisplayName(first, last, methods.FirstOrDefault().Value), JobTitle = req.JobTitle, CompanyId = req.CompanyId,
                OwnerUserId = u.UserId, Source = req.Source, Notes = req.Notes, CreatedBy = u.UserId, CreatedAt = now, UpdatedAt = now,
            };
            db.Contacts.Add(c);
            var seenPrimary = new HashSet<MethodType>();
            foreach (var (type, value, norm, primary) in methods)
                db.Methods.Add(new ContactMethod { ContactId = c.Id, Type = type, Value = value, NormalizedValue = norm, IsPrimary = primary && seenPrimary.Add(type) });
            foreach (var a in (req.Associations ?? []).DistinctBy(a => (a.ProductId, a.CampaignId))) db.Associations.Add(new ContactAssociation { ContactId = c.Id, ProductId = a.ProductId, CampaignId = a.CampaignId });
            foreach (var t in (req.Tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 60).Distinct()) await AddTag(db, c.Id, t, ct);
            foreach (var k in req.Consents ?? []) db.Consents.Add(NewConsent(c.Id, k, now, u.UserId));
            db.Interactions.Add(new Interaction { ContactId = c.Id, Type = InteractionType.System, Summary = "Contact created", OccurredAt = now, CreatedBy = u.UserId });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUnique(ex)) { return Results.Problem(statusCode: 409, title: "A contact with this email/phone already exists"); }
            await audit.WriteAsync(new AuditEntry("crm.contact.created", "Contact", c.Id.ToString(), Summary: c.DisplayName, After: new { c.Source, Methods = methods.Count, Consents = req.Consents?.Length ?? 0 }), ct);
            return Results.Created($"/api/v1/crm/contacts/{c.Id}", new { c.Id, c.DisplayName });
        });

        g.MapGet("/contacts/{id:guid}", async (Guid id, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmRead, ct, includeMerged: true);
            if (c is null) return Results.NotFound();
            if (c.MergedIntoId is not null) return Results.Problem(statusCode: 410, title: "This contact was merged into another record", extensions: new Dictionary<string, object?> { ["mergedIntoId"] = c.MergedIntoId });
            var methods = await db.Methods.AsNoTracking().Where(m => m.ContactId == id).OrderByDescending(m => m.IsPrimary).ToListAsync(ct);
            var tags = await db.ContactTags.AsNoTracking().Where(t => t.ContactId == id).Join(db.Tags, t => t.TagId, tg => tg.Id, (t, tg) => tg.Name).OrderBy(x => x).ToListAsync(ct);
            var assoc = await db.Associations.AsNoTracking().Where(a => a.ContactId == id).Select(a => new { a.Id, a.ProductId, a.CampaignId }).ToListAsync(ct);
            var inter = await db.Interactions.AsNoTracking().Where(i => i.ContactId == id).OrderByDescending(i => i.OccurredAt).ThenByDescending(i => i.Id).Take(100).ToListAsync(ct);
            var consent = (await db.Consents.AsNoTracking().Where(k => k.ContactId == id).ToListAsync(ct))
                .GroupBy(k => (k.Channel, k.Purpose)).Select(gr => gr.OrderByDescending(k => k.RecordedAt).ThenByDescending(k => k.Id).First())
                .Select(k => new { k.Channel, k.Purpose, k.Status, k.Source, k.Evidence, k.RecordedAt }).ToList();
            var company = c.CompanyId is null ? null : await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.CompanyId, ct);
            return Results.Ok(new { contact = c, company, methods, tags, associations = assoc, consent, interactions = inter.Select(i => new { i.Id, i.Type, i.Summary, i.OccurredAt, i.ProductId, i.CreatedBy }) });
        });

        g.MapPut("/contacts/{id:guid}", async (Guid id, UpdateContact req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            if (c is null) return Results.NotFound();
            if (req.CompanyId is { } coid && !await db.Companies.AnyAsync(x => x.Id == coid, ct)) return Results.Problem(statusCode: 400, title: "Company not found");
            var before = new { c.FirstName, c.LastName, c.JobTitle, c.CompanyId, c.OwnerUserId };
            if (req.FirstName is not null) c.FirstName = req.FirstName.Trim();
            if (req.LastName is not null) c.LastName = req.LastName.Trim();
            c.DisplayName = Normalizer.DisplayName(c.FirstName, c.LastName, c.DisplayName);
            if (req.JobTitle is not null) c.JobTitle = req.JobTitle;
            if (req.CompanyId is not null) c.CompanyId = req.CompanyId;
            if (req.OwnerUserId is not null) c.OwnerUserId = req.OwnerUserId;
            if (req.Source is not null) c.Source = req.Source;
            if (req.Notes is not null) c.Notes = req.Notes;
            c.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.contact.updated", "Contact", id.ToString(), Summary: c.DisplayName, Before: before, After: new { c.FirstName, c.LastName, c.JobTitle, c.CompanyId, c.OwnerUserId }), ct);
            return Results.Ok(new { c.Id, c.DisplayName });
        });

        g.MapPost("/contacts/{id:guid}/methods", async (Guid id, MethodIn req, CrmDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            if (c is null) return Results.NotFound();
            var norm = Normalizer.Method(req.Type, req.Value);
            if (norm is null) return Results.Problem(statusCode: 400, title: $"'{req.Value}' is not a valid {req.Type}");
            var owner = await db.Methods.AsNoTracking().Where(m => m.Type == req.Type && m.NormalizedValue == norm).Select(m => (Guid?)m.ContactId).FirstOrDefaultAsync(ct);
            if (owner is not null) return Results.Problem(statusCode: 409, title: owner == id ? "The contact already has this address" : "Another contact already has this address", extensions: new Dictionary<string, object?> { ["existingContactId"] = owner });
            if (req.IsPrimary) foreach (var m in await db.Methods.Where(m => m.ContactId == id && m.Type == req.Type && m.IsPrimary).ToListAsync(ct)) m.IsPrimary = false;
            db.Methods.Add(new ContactMethod { ContactId = id, Type = req.Type, Value = req.Value.Trim(), NormalizedValue = norm, IsPrimary = req.IsPrimary });
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException ex) when (IsUnique(ex)) { return Results.Problem(statusCode: 409, title: "Another contact already has this address"); }
            await audit.WriteAsync(new AuditEntry("crm.contact.method-added", "Contact", id.ToString(), Summary: req.Type.ToString()), ct);
            return Results.NoContent();
        });

        g.MapDelete("/contacts/{id:guid}/methods/{methodId:guid}", async (Guid id, Guid methodId, CrmDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            if (c is null) return Results.NotFound();
            var m = await db.Methods.FirstOrDefaultAsync(x => x.Id == methodId && x.ContactId == id, ct);
            if (m is null) return Results.NotFound();
            db.Methods.Remove(m);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.contact.method-removed", "Contact", id.ToString(), Summary: m.Type.ToString()), ct);
            return Results.NoContent();
        });

        g.MapPut("/contacts/{id:guid}/tags/{tag}", async (Guid id, string tag, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            var t = tag.Trim().ToLowerInvariant();
            if (c is null) return Results.NotFound();
            if (t.Length is 0 or > 60) return Results.Problem(statusCode: 400, title: "Tags are 1-60 characters");
            await AddTag(db, id, t, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapDelete("/contacts/{id:guid}/tags/{tag}", async (Guid id, string tag, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            if (c is null) return Results.NotFound();
            var t = tag.Trim().ToLowerInvariant();
            var link = await db.ContactTags.FirstOrDefaultAsync(x => x.ContactId == id && db.Tags.Any(tg => tg.Id == x.TagId && tg.Name == t), ct);
            if (link is not null) { db.ContactTags.Remove(link); await db.SaveChangesAsync(ct); }
            return Results.NoContent();
        });

        g.MapPost("/contacts/{id:guid}/associations", async (Guid id, AssocIn req, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            if (c is null) return Results.NotFound();
            if (!await u.CanAccessProductAsync(req.ProductId, ct)) return Results.Problem(statusCode: 400, title: "Unknown or inaccessible product");
            if (!await db.Associations.AnyAsync(a => a.ContactId == id && a.ProductId == req.ProductId && a.CampaignId == req.CampaignId, ct))
            {
                db.Associations.Add(new ContactAssociation { ContactId = id, ProductId = req.ProductId, CampaignId = req.CampaignId });
                try { await db.SaveChangesAsync(ct); } catch (DbUpdateException ex) when (IsUnique(ex)) { }
            }
            return Results.NoContent();
        });

        g.MapPost("/contacts/{id:guid}/interactions", async (Guid id, InteractionIn req, CrmDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            if (c is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Summary) || req.Summary.Length > 4000) return Results.Problem(statusCode: 400, title: "A summary (max 4000 characters) is required");
            if (req.Type == InteractionType.System) return Results.Problem(statusCode: 400, title: "System entries are written by the platform");
            db.Interactions.Add(new Interaction { ContactId = id, Type = req.Type, Summary = req.Summary.Trim(), ProductId = req.ProductId, OccurredAt = clock.UtcNow, CreatedBy = u.UserId });
            c.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task AddTag(CrmDbContext db, Guid contactId, string name, CancellationToken ct)
    {
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct) ?? db.Tags.Add(new Tag { Name = name }).Entity;
        if (!await db.ContactTags.AnyAsync(x => x.ContactId == contactId && x.TagId == tag.Id, ct) && !db.ContactTags.Local.Any(x => x.ContactId == contactId && x.TagId == tag.Id))
            db.ContactTags.Add(new ContactTag { ContactId = contactId, TagId = tag.Id });
    }

    // ---------- consent ----------
    internal static string? ConsentError(ConsentIn k)
    {
        if (string.IsNullOrWhiteSpace(k.Source)) return "Consent needs a source (for example 'web form', 'verbal', 'import').";
        if (k.Status == ConsentStatus.Granted && string.IsNullOrWhiteSpace(k.Evidence)) return "A consent grant needs evidence of what the person agreed to.";
        return null;
    }

    internal static ConsentRecord NewConsent(Guid contactId, ConsentIn k, DateTimeOffset at, Guid? by) =>
        new() { ContactId = contactId, Channel = k.Channel, Purpose = k.Purpose, Status = k.Status, Source = k.Source.Trim(), Evidence = k.Evidence, RecordedAt = at, RecordedBy = by };

    private static void MapConsent(RouteGroupBuilder g)
    {
        g.MapPost("/contacts/{id:guid}/consent", async (Guid id, ConsentIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmWrite, ct);
            if (c is null) return Results.NotFound();
            if (ConsentError(req) is { } err) return Results.Problem(statusCode: 400, title: err);
            var now = clock.UtcNow;
            db.Consents.Add(NewConsent(id, req, now, u.UserId));
            db.Interactions.Add(new Interaction { ContactId = id, Type = InteractionType.System, Summary = $"Consent {req.Status.ToString().ToLowerInvariant()}: {req.Channel}/{req.Purpose} ({req.Source})", OccurredAt = now, CreatedBy = u.UserId });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry($"crm.consent.{req.Status.ToString().ToLowerInvariant()}", "Contact", id.ToString(), Summary: $"{req.Channel}/{req.Purpose}", After: new { req.Source, req.Evidence }), ct);
            return Results.NoContent();
        });

        g.MapGet("/contacts/{id:guid}/consent", async (Guid id, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmRead, ct);
            if (c is null) return Results.NotFound();
            return Results.Ok(await db.Consents.AsNoTracking().Where(k => k.ContactId == id).OrderByDescending(k => k.RecordedAt).ThenByDescending(k => k.Id)
                .Select(k => new { k.Id, k.Channel, k.Purpose, k.Status, k.Source, k.Evidence, k.RecordedAt, k.RecordedBy }).ToListAsync(ct));
        });

        g.MapGet("/contacts/{id:guid}/can-contact", async (Guid id, ContactChannel channel, ContactPurpose purpose, CrmDbContext db, ICurrentUser u, IContactPolicy policy, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmRead, ct);
            if (c is null) return Results.NotFound();
            return Results.Ok(await policy.CanContactAsync(id, channel, purpose, ct));
        });

        // Opt-out / bounce / complaint at address level. Applies even if the address later moves to another contact.
        g.MapPost("/suppressions", async (SuppressIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmWrite)) return Results.Forbid();
            var norm = Normalizer.Method(Normalizer.MethodFor(req.Channel), req.Value);
            if (norm is null) return Results.Problem(statusCode: 400, title: "Not a valid address for that channel");
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.Problem(statusCode: 400, title: "A reason is required");
            if (!await db.Suppressions.AnyAsync(s => s.Channel == req.Channel && s.NormalizedValue == norm, ct))
            {
                db.Suppressions.Add(new Suppression { Channel = req.Channel, NormalizedValue = norm, Reason = req.Reason.Trim(), CreatedAt = clock.UtcNow, CreatedBy = u.UserId });
                try { await db.SaveChangesAsync(ct); } catch (DbUpdateException ex) when (IsUnique(ex)) { }
                await audit.WriteAsync(new AuditEntry("crm.suppression.added", "Suppression", norm, Summary: $"{req.Channel}: {req.Reason}"), ct);
            }
            return Results.NoContent();
        });

        g.MapGet("/suppressions", async (CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
            u.HasPermission(Permissions.CrmRead)
                ? Results.Ok(await db.Suppressions.AsNoTracking().OrderByDescending(s => s.CreatedAt).Take(500).Select(s => new { s.Id, s.Channel, s.NormalizedValue, s.Reason, s.CreatedAt }).ToListAsync(ct))
                : Results.Forbid());
    }

    // ---------- duplicates & merge ----------
    private static void MapMerge(RouteGroupBuilder g)
    {
        g.MapGet("/contacts/{id:guid}/duplicates", async (Guid id, CrmDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await Find(db, u, id, Permissions.CrmRead, ct);
            if (c is null) return Results.NotFound();
            var mine = await db.Methods.AsNoTracking().Where(m => m.ContactId == id).ToListAsync(ct);
            var candidates = new Dictionary<Guid, List<string>>();
            void Add(Guid cid, string why) { if (cid != id) { if (!candidates.TryGetValue(cid, out var l)) candidates[cid] = l = []; l.Add(why); } }

            // Exact address matches cannot exist (unique index), but a phone and WhatsApp number can be the same person's number.
            foreach (var m in mine.Where(m => m.Type != MethodType.Email))
                foreach (var other in await db.Methods.AsNoTracking().Where(o => o.Type != MethodType.Email && o.NormalizedValue == m.NormalizedValue && o.ContactId != id).ToListAsync(ct))
                    Add(other.ContactId, "same phone number");
            // Same email local-part + company, or same full name + same company.
            if (c.CompanyId is not null && c.DisplayName.Contains(' '))
                foreach (var other in await db.Contacts.AsNoTracking().Where(o => o.Id != id && o.MergedIntoId == null && o.CompanyId == c.CompanyId && o.DisplayName == c.DisplayName).ToListAsync(ct))
                    Add(other.Id, "same name at the same company");
            foreach (var m in mine.Where(m => m.Type == MethodType.Email))
            {
                var local = m.NormalizedValue.Split('@')[0];
                foreach (var other in await db.Methods.AsNoTracking().Where(o => o.Type == MethodType.Email && o.ContactId != id && o.NormalizedValue.StartsWith(local + "@")).ToListAsync(ct))
                    Add(other.ContactId, "similar email address");
            }
            var ids = candidates.Keys.ToList();
            var visible = await (await VisibleAsync(db, u, false, ct)).AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            return Results.Ok(visible.Select(v => new { v.Id, v.DisplayName, reasons = candidates[v.Id].Distinct() }));
        });

        // Survivor = {id}; the source's addresses, tags, product links, interactions and consent history all move to the survivor.
        g.MapPost("/contacts/{id:guid}/merge", async (Guid id, MergeIn req, CrmDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (req.SourceId == id) return Results.Problem(statusCode: 400, title: "Cannot merge a contact into itself");
            var survivor = await Find(db, u, id, Permissions.CrmWrite, ct);
            var source = await Find(db, u, req.SourceId, Permissions.CrmWrite, ct);
            if (survivor is null || source is null) return Results.NotFound();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.UtcNow;

            var methods = await db.Methods.Where(m => m.ContactId == source.Id).ToListAsync(ct);
            var survivorTypes = (await db.Methods.Where(m => m.ContactId == id && m.IsPrimary).Select(m => m.Type).ToListAsync(ct)).ToHashSet();
            foreach (var m in methods) { m.ContactId = id; if (m.IsPrimary && survivorTypes.Contains(m.Type)) m.IsPrimary = false; }

            var survivorTags = (await db.ContactTags.Where(t => t.ContactId == id).Select(t => t.TagId).ToListAsync(ct)).ToHashSet();
            var sourceTags = await db.ContactTags.Where(t => t.ContactId == source.Id).ToListAsync(ct);
            foreach (var t in sourceTags) { db.ContactTags.Remove(t); if (survivorTags.Add(t.TagId)) db.ContactTags.Add(new ContactTag { ContactId = id, TagId = t.TagId }); }

            var survivorAssoc = (await db.Associations.Where(a => a.ContactId == id).Select(a => new { a.ProductId, a.CampaignId }).ToListAsync(ct)).Select(a => (a.ProductId, a.CampaignId)).ToHashSet();
            var sourceAssoc = await db.Associations.Where(a => a.ContactId == source.Id).ToListAsync(ct);
            foreach (var a in sourceAssoc) { if (survivorAssoc.Add((a.ProductId, a.CampaignId))) a.ContactId = id; else db.Associations.Remove(a); }

            var interactions = await db.Interactions.Where(i => i.ContactId == source.Id).ToListAsync(ct);
            foreach (var i in interactions) i.ContactId = id;
            var consents = await db.Consents.Where(k => k.ContactId == source.Id).ToListAsync(ct);
            foreach (var k in consents) k.ContactId = id;   // history preserved with original timestamps

            if (string.IsNullOrWhiteSpace(survivor.JobTitle)) survivor.JobTitle = source.JobTitle;
            survivor.CompanyId ??= source.CompanyId;
            survivor.OwnerUserId ??= source.OwnerUserId;
            if (string.IsNullOrWhiteSpace(survivor.Source)) survivor.Source = source.Source;
            if (!string.IsNullOrWhiteSpace(source.Notes)) survivor.Notes = string.IsNullOrWhiteSpace(survivor.Notes) ? source.Notes : survivor.Notes + "\n--- merged from " + source.DisplayName + " ---\n" + source.Notes;
            survivor.UpdatedAt = now;
            source.MergedIntoId = id; source.UpdatedAt = now;
            db.Interactions.Add(new Interaction { ContactId = id, Type = InteractionType.System, Summary = $"Merged '{source.DisplayName}' into this contact", OccurredAt = now, CreatedBy = u.UserId });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await audit.WriteAsync(new AuditEntry("crm.contact.merged", "Contact", id.ToString(), Summary: $"{source.DisplayName} -> {survivor.DisplayName}",
                After: new { SourceId = source.Id, Methods = methods.Count, Tags = sourceTags.Count, Associations = sourceAssoc.Count, Interactions = interactions.Count, Consents = consents.Count }), ct);
            return Results.Ok(new { survivor.Id, mergedSourceId = source.Id });
        });
    }
}
