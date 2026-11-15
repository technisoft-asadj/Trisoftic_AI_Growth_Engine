using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Messaging;

public sealed record OutgoingEmail(string From, string To, string Subject, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>One adapter per email service. SMTP covers most (Amazon SES, SendGrid, Mailgun and others all offer an SMTP endpoint).</summary>
public interface IEmailProvider
{
    bool IsConfigured { get; }
    string From { get; }
    /// <summary>Returns the provider's message id. Throws <see cref="TransientJobException"/> to retry later or <see cref="PermanentJobException"/> if the message can never be sent.</summary>
    Task<string> SendAsync(OutgoingEmail message, CancellationToken ct);
}

public sealed class SmtpEmailProvider(IConfiguration cfg) : IEmailProvider
{
    public bool IsConfigured => !string.IsNullOrEmpty(cfg["Messaging:Smtp:Host"]) && !string.IsNullOrEmpty(From);
    public string From => cfg["Messaging:From"] ?? "";

    public async Task<string> SendAsync(OutgoingEmail m, CancellationToken ct)
    {
        if (!IsConfigured) throw new PermanentJobException("No email provider is configured (Messaging:Smtp:Host and Messaging:From)");
        using var client = new SmtpClient(cfg["Messaging:Smtp:Host"], int.TryParse(cfg["Messaging:Smtp:Port"], out var p) ? p : 587)
        {
            EnableSsl = !string.Equals(cfg["Messaging:Smtp:UseSsl"], "false", StringComparison.OrdinalIgnoreCase), DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 30_000,
        };
        if (!string.IsNullOrEmpty(cfg["Messaging:Smtp:User"])) client.Credentials = new NetworkCredential(cfg["Messaging:Smtp:User"], cfg["Messaging:Smtp:Password"]);
        using var mail = new MailMessage(m.From, m.To, m.Subject, m.Body) { IsBodyHtml = false };
        var id = $"<{Guid.NewGuid():N}@{new MailAddress(m.From).Host}>";
        mail.Headers.Add("Message-ID", id);
        foreach (var (k, v) in m.Headers) mail.Headers.Add(k, v);
        try { await client.SendMailAsync(mail, ct); }
        catch (SmtpFailedRecipientException ex) { throw new PermanentJobException($"The server refused the recipient: {ex.Message}", ex); }
        catch (SmtpException ex) when (ex.StatusCode is SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxNameNotAllowed or SmtpStatusCode.UserNotLocalTryAlternatePath or SmtpStatusCode.ClientNotPermitted)
        { throw new PermanentJobException($"The server refused the message: {ex.Message}", ex); }
        catch (Exception ex) when (ex is SmtpException or IOException or System.Net.Sockets.SocketException) { throw new TransientJobException("Could not send via SMTP: " + ex.Message, inner: ex); }
        return id;
    }
}

/// <summary>Signed links that let a person unsubscribe without logging in. Nothing but the address and channel is inside, and it cannot be forged.</summary>
public static class UnsubscribeLinks
{
    private static string B64(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static byte[] FromB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
    private static string Mac(string secret, string address) => B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("unsub:email:" + address)));

    public static string Create(string address, string secret) => B64(Encoding.UTF8.GetBytes(address)) + "." + Mac(secret, address);

    public static string? Verify(string? token, string? secret)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret) || token.Length > 600) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0) return null;
        string address;
        try { address = Encoding.UTF8.GetString(FromB64(token[..dot])); } catch (FormatException) { return null; }
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Mac(secret, address)), Encoding.UTF8.GetBytes(token[(dot + 1)..])) ? address : null;
    }

    public static string Mask(string address)
    {
        var at = address.IndexOf('@');
        return at <= 1 ? address : address[0] + new string('*', Math.Min(at - 1, 6)) + address[at..];
    }
}

public sealed class MessageOutbox(MessagingDbContext db, IJobQueue jobs, IClock clock) : IMessageOutbox
{
    public const string SendJob = "messaging.send-email";

    public async Task<QueuedEmail> QueueEmailAsync(EmailRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Subject) || r.Subject.Length > 300) throw new ArgumentException("A subject (max 300 characters) is required");
        if (string.IsNullOrWhiteSpace(r.Body) || r.Body.Length > 100_000) throw new ArgumentException("A body (max 100,000 characters) is required");
        if (string.IsNullOrWhiteSpace(r.DedupKey) || r.DedupKey.Length > 200) throw new ArgumentException("A dedup key is required");
        if (r.ContactId is null && string.IsNullOrWhiteSpace(r.ToAddress)) throw new ArgumentException("A contact or an address is required");
        if (r.Purpose == ContactPurpose.Marketing && r.ContactId is null) throw new ArgumentException("Marketing email needs a contact with recorded consent");

        var existing = await db.Emails.AsNoTracking().Where(m => m.DedupKey == r.DedupKey).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return new QueuedEmail(id, true);

        var msg = new EmailMessage
        {
            ProductId = r.ProductId, ContactId = r.ContactId, ToAddress = r.ToAddress?.Trim(), Purpose = r.Purpose, Subject = r.Subject.Trim(), Body = r.Body, Status = MessageStatus.Queued,
            DedupKey = r.DedupKey, SubjectType = r.SubjectType, SubjectId = r.SubjectId, QueuedAt = clock.UtcNow,
        };
        db.Emails.Add(msg);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return new QueuedEmail(await db.Emails.AsNoTracking().Where(m => m.DedupKey == r.DedupKey).Select(m => m.Id).FirstAsync(ct), true);
        }
        await jobs.EnqueueAsync(new JobRequest(SendJob, $"{{\"id\":\"{msg.Id}\"}}", IdempotencyKey: "email:" + msg.Id, ProductId: r.ProductId, MaxAttempts: 5), ct);
        return new QueuedEmail(msg.Id, false);
    }
}

/// <summary>
/// Sends one queued email. The consent decision is made here, immediately before sending, never earlier:
/// someone who unsubscribed after the email was queued must not receive it.
/// </summary>
public sealed class SendEmailHandler(MessagingDbContext db, IContactPolicy contacts, IAddressPolicy addresses, IEmailProvider provider, IConfiguration cfg, IClock clock) : IJobHandler
{
    public string Type => MessageOutbox.SendJob;
    public PauseScope? PauseScope => BuildingBlocks.Abstractions.PauseScope.Messaging;

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        var id = System.Text.Json.JsonDocument.Parse(c.PayloadJson).RootElement.GetProperty("id").GetGuid();
        var m = await db.Emails.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null || m.Status != MessageStatus.Queued) return;   // already handled

        var decision = m.ContactId is { } cid
            ? await contacts.CanContactAsync(cid, ContactChannel.Email, m.Purpose, ct)
            : await addresses.CanContactAddressAsync(ContactChannel.Email, m.ToAddress!, m.Purpose, ct);
        if (!decision.Allowed || string.IsNullOrEmpty(decision.Address)) { await Finish(m, MessageStatus.Blocked, decision.Reason, ct); return; }

        var headers = new Dictionary<string, string>();
        var body = m.Body;
        if (m.Purpose == ContactPurpose.Marketing)
        {
            // Marketing mail must be identifiable and must carry a working way out. Without those settings it is not sent at all.
            var baseUrl = cfg["Messaging:PublicBaseUrl"]?.TrimEnd('/'); var secret = cfg["Messaging:LinkSecret"]; var footer = cfg["Messaging:FooterText"];
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(footer))
            { await Finish(m, MessageStatus.Blocked, "Marketing email needs Messaging:PublicBaseUrl, LinkSecret and FooterText (sender identity and postal address) to be configured", ct); return; }
            var link = $"{baseUrl}/unsubscribe/{UnsubscribeLinks.Create(decision.Address, secret)}";
            headers["List-Unsubscribe"] = $"<{baseUrl}/api/v1/public/unsubscribe/{UnsubscribeLinks.Create(decision.Address, secret)}>";
            headers["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click";
            body += $"\n\n--\n{footer}\nTo stop receiving these emails: {link}";
        }
        if (!provider.IsConfigured) { await Finish(m, MessageStatus.Failed, "No email provider is configured", ct); return; }

        try
        {
            m.ProviderMessageId = await provider.SendAsync(new OutgoingEmail(provider.From, decision.Address, m.Subject, body, headers), ct);
        }
        catch (PermanentJobException ex) { await Finish(m, MessageStatus.Failed, ex.Message, ct); return; }
        catch (TransientJobException) when (c.Attempt >= c.MaxAttempts) { await Finish(m, MessageStatus.Failed, "Gave up after repeated temporary failures", ct); throw; }
        await Finish(m, MessageStatus.Sent, decision.Reason, ct);
    }

    private async Task Finish(EmailMessage m, MessageStatus status, string? reason, CancellationToken ct)
    {
        m.Status = status; m.Reason = reason is { Length: > 1000 } ? reason[..1000] : reason; if (status == MessageStatus.Sent) m.SentAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
