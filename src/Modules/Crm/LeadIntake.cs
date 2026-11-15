using Microsoft.EntityFrameworkCore;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public sealed record IntakeRow(string? FirstName, string? LastName, string? Email, string? Phone, string? Company, string? JobTitle, string? Message);
public sealed record IntakeConsent(bool Given, string Source, string? Evidence);
public sealed record IntakeResult(Guid ContactId, Guid LeadId, bool ContactCreated, bool LeadCreated);

/// <summary>
/// One code path for every way a lead arrives (public form, CSV import, API), so dedupe, consent capture and scoring behave identically.
/// Never assumes consent: marketing opt-in is recorded only when the caller supplies evidence of it.
/// </summary>
public static class LeadIntake
{
    public static async Task<Result<IntakeResult>> RunAsync(CrmDbContext db, IntakeRow row, Guid productId, Guid? campaignId, Guid? formId, string source,
        IntakeConsent? consent, Guid? ownerUserId, Guid? actor, DateTimeOffset now, CancellationToken ct)
    {
        string? email = null, phone = null;
        if (!string.IsNullOrWhiteSpace(row.Email)) { email = Normalizer.Method(MethodType.Email, row.Email); if (email is null) return new Error("intake.bad_email", $"'{row.Email}' is not a valid email address"); }
        if (!string.IsNullOrWhiteSpace(row.Phone)) { phone = Normalizer.Method(MethodType.Phone, row.Phone); if (phone is null) return new Error("intake.bad_phone", $"'{row.Phone}' is not a valid phone number"); }
        if (email is null && phone is null) return new Error("intake.no_contact_method", "An email address or phone number is required");
        if (consent is { Given: true } && string.IsNullOrWhiteSpace(consent.Evidence)) return new Error("intake.consent_evidence", "Consent was claimed without evidence of what the person agreed to");

        // existing person? (email first, then phone)
        Guid? contactId = null;
        if (email is not null) contactId = await db.Methods.AsNoTracking().Where(m => m.Type == MethodType.Email && m.NormalizedValue == email).Select(m => (Guid?)m.ContactId).FirstOrDefaultAsync(ct);
        if (contactId is null && phone is not null) contactId = await db.Methods.AsNoTracking().Where(m => m.Type == MethodType.Phone && m.NormalizedValue == phone).Select(m => (Guid?)m.ContactId).FirstOrDefaultAsync(ct);
        if (contactId is { } cid)   // follow merges
        {
            var merged = await db.Contacts.AsNoTracking().Where(c => c.Id == cid).Select(c => c.MergedIntoId).FirstOrDefaultAsync(ct);
            if (merged is not null) contactId = merged;
        }

        var contactCreated = false;
        Contact contact;
        if (contactId is { } existingId) contact = await db.Contacts.FirstAsync(c => c.Id == existingId, ct);
        else
        {
            var first = (row.FirstName ?? "").Trim(); var last = (row.LastName ?? "").Trim();
            contact = new Contact
            {
                FirstName = first, LastName = last, DisplayName = Normalizer.DisplayName(first, last, email ?? phone), JobTitle = row.JobTitle, Source = source,
                OwnerUserId = ownerUserId, CreatedBy = actor, CreatedAt = now, UpdatedAt = now,
            };
            db.Contacts.Add(contact);
            if (email is not null) db.Methods.Add(new ContactMethod { ContactId = contact.Id, Type = MethodType.Email, Value = row.Email!.Trim(), NormalizedValue = email, IsPrimary = true });
            if (phone is not null) db.Methods.Add(new ContactMethod { ContactId = contact.Id, Type = MethodType.Phone, Value = row.Phone!.Trim(), NormalizedValue = phone, IsPrimary = true });
            db.Interactions.Add(new Interaction { ContactId = contact.Id, Type = InteractionType.System, Summary = $"Contact created from {source}", OccurredAt = now, CreatedBy = actor });
            contactCreated = true;
        }

        if (!string.IsNullOrWhiteSpace(row.Company) && contact.CompanyId is null)
        {
            var name = row.Company.Trim();
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), ct);
            if (company is null) { company = new Company { Name = name.Length > 300 ? name[..300] : name, CreatedBy = actor, CreatedAt = now, UpdatedAt = now }; db.Companies.Add(company); }
            contact.CompanyId = company.Id;
        }
        if (string.IsNullOrWhiteSpace(contact.JobTitle) && !string.IsNullOrWhiteSpace(row.JobTitle)) contact.JobTitle = row.JobTitle;

        if (!await db.Associations.AnyAsync(a => a.ContactId == contact.Id && a.ProductId == productId && a.CampaignId == campaignId, ct)
            && !db.Associations.Local.Any(a => a.ContactId == contact.Id && a.ProductId == productId && a.CampaignId == campaignId))
            db.Associations.Add(new ContactAssociation { ContactId = contact.Id, ProductId = productId, CampaignId = campaignId });

        if (consent is { Given: true } && email is not null)
        {
            var latest = contactCreated ? null : await db.Consents.AsNoTracking().Where(k => k.ContactId == contact.Id && k.Channel == ContactChannel.Email && k.Purpose == ContactPurpose.Marketing)
                .OrderByDescending(k => k.RecordedAt).ThenByDescending(k => k.Id).Select(k => (ConsentStatus?)k.Status).FirstOrDefaultAsync(ct);
            if (latest != ConsentStatus.Granted)
                db.Consents.Add(new ConsentRecord { ContactId = contact.Id, Channel = ContactChannel.Email, Purpose = ContactPurpose.Marketing, Status = ConsentStatus.Granted, Source = consent.Source, Evidence = consent.Evidence, RecordedAt = now, RecordedBy = actor });
        }

        var open = await db.Leads.FirstOrDefaultAsync(l => l.ContactId == contact.Id && l.ProductId == productId && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified), ct);
        if (open is not null)
        {
            db.Interactions.Add(new Interaction { ContactId = contact.Id, Type = InteractionType.System, ProductId = productId, OccurredAt = now, CreatedBy = actor,
                Summary = "Enquired again via " + source + (string.IsNullOrWhiteSpace(row.Message) ? "" : ": " + Trunc(row.Message, 500)) });
            open.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return new IntakeResult(contact.Id, open.Id, contactCreated, false);
        }

        var lead = new Lead
        {
            ContactId = contact.Id, ProductId = productId, CampaignId = campaignId, FormId = formId, Source = source, Message = row.Message is null ? null : Trunc(row.Message, 4000),
            OwnerUserId = ownerUserId, CreatedAt = now, UpdatedAt = now,
        };
        lead.Score = (await ScoreAsync(db, lead, contact, ct)).Score;
        db.Leads.Add(lead);
        db.Interactions.Add(new Interaction { ContactId = contact.Id, Type = InteractionType.System, ProductId = productId, OccurredAt = now, CreatedBy = actor, Summary = $"Lead created via {source} (score {lead.Score})" });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new Error("intake.conflict", "This person was just added by someone else; submit again", ErrorKind.Conflict);
        }
        return new IntakeResult(contact.Id, lead.Id, contactCreated, true);
    }

    public static async Task<(int Score, List<ScoreHit> Hits)> ScoreAsync(CrmDbContext db, Lead lead, Contact contact, CancellationToken ct)
    {
        var rules = await db.ScoreRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var tags = contact.Id == Guid.Empty ? [] : await db.ContactTags.AsNoTracking().Where(t => t.ContactId == contact.Id).Join(db.Tags, t => t.TagId, tg => tg.Id, (t, tg) => tg.Name).ToListAsync(ct);
        var hasPhone = db.Methods.Local.Any(m => m.ContactId == contact.Id && m.Type != MethodType.Email) || await db.Methods.AsNoTracking().AnyAsync(m => m.ContactId == contact.Id && m.Type != MethodType.Email, ct);
        return Scorer.Evaluate(rules, lead.ProductId, new ScoreInput(lead.Source, contact.CompanyId is not null, hasPhone, contact.JobTitle, tags, lead.Message));
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
