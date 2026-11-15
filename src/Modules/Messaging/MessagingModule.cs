using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Messaging;

public static class MessagingModule
{
    /// <summary>Rate-limit policy the host must register for the anonymous unsubscribe routes.</summary>
    public const string PublicPolicy = "public-messaging";

    private sealed record BounceIn(string? Type, string? Address);

    public static IServiceCollection AddMessagingModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<MessagingDbContext>(c, MessagingDbContext.Schema);
        s.AddScoped<IEmailProvider, SmtpEmailProvider>();
        s.AddScoped<IMessageOutbox, MessageOutbox>();
        s.AddScoped<IJobHandler, SendEmailHandler>();
        return s;
    }

    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/messaging").RequireAuthorization().WithTags("Messaging");

        g.MapGet("/status", (ICurrentUser u, IEmailProvider provider, IConfiguration cfg) =>
        {
            if (!u.HasPermission(Permissions.LeadsManage) && !u.HasPermission(Permissions.CrmWrite)) return Results.Forbid();
            return Results.Ok(new
            {
                providerConfigured = provider.IsConfigured, from = provider.IsConfigured ? provider.From : null,
                // Marketing mail is refused unless these are set: sender identity, a working unsubscribe link, a postal footer.
                marketingReady = provider.IsConfigured && !string.IsNullOrEmpty(cfg["Messaging:PublicBaseUrl"]) && !string.IsNullOrEmpty(cfg["Messaging:LinkSecret"]) && !string.IsNullOrWhiteSpace(cfg["Messaging:FooterText"]),
            });
        });

        g.MapGet("/messages", async (MessagingDbContext db, ICurrentUser u, MessageStatus? status, Guid? productId, int? take, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.CrmWrite)) return Results.Forbid();
            var q = db.Emails.AsNoTracking().AsQueryable();
            var scope = await u.AccessibleProductIdsAsync(ct);
            if (scope is not null) q = q.Where(m => m.ProductId != null && scope.Contains(m.ProductId.Value));
            if (productId is not null) q = q.Where(m => m.ProductId == productId);
            if (status is not null) q = q.Where(m => m.Status == status);
            var rows = await q.OrderByDescending(m => m.Id).Take(Math.Clamp(take ?? 100, 1, 300)).ToListAsync(ct);
            return Results.Ok(rows.Select(m => new { m.Id, m.ProductId, m.ContactId, to = m.ToAddress is null ? null : UnsubscribeLinks.Mask(m.ToAddress), m.Purpose, m.Subject, m.Status, m.Reason, m.SubjectType, m.SubjectId, m.QueuedAt, m.SentAt }));
        });

        // Anonymous: the link in an email. One click, no login, only ever stops marketing for that address.
        var pub = app.MapGroup("/api/v1/public/unsubscribe").AllowAnonymous().RequireRateLimiting(PublicPolicy).WithTags("Public unsubscribe");
        pub.MapGet("/{token}", (string token, IConfiguration cfg) =>
            UnsubscribeLinks.Verify(token, cfg["Messaging:LinkSecret"]) is { } address ? Results.Ok(new { address = UnsubscribeLinks.Mask(address) }) : Results.NotFound());
        pub.MapPost("/{token}", async (string token, IConfiguration cfg, IOptOutService optOut, CancellationToken ct) =>
        {
            if (UnsubscribeLinks.Verify(token, cfg["Messaging:LinkSecret"]) is not { } address) return Results.NotFound();
            await optOut.OptOutAsync(ContactChannel.Email, address, "unsubscribe link", marketingOnly: true, ct);
            return Results.NoContent();   // same answer whether or not anything changed
        });

        // Bounces and spam complaints from the email service (forwarded by n8n or a small function). Signed like every other webhook.
        app.MapPost("/api/v1/webhooks/email/{name}", async (string name, HttpRequest req, IConfiguration cfg, IClock clock, IOptOutService optOut, CancellationToken ct) =>
        {
            var secret = System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,40}$") ? cfg[$"Messaging:Webhooks:{name}:Secret"] : null;
            if (string.IsNullOrEmpty(secret)) return Results.NotFound();
            if (req.ContentLength is null or > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            string body; using (var sr = new StreamReader(req.Body)) body = await sr.ReadToEndAsync(ct);
            if (!WebhookSigner.Verify(secret, req.Headers["X-TE-Timestamp"].ToString(), body, req.Headers["X-TE-Signature"].ToString(), clock.UtcNow)) return Results.Unauthorized();

            BounceIn? b; try { b = JsonSerializer.Deserialize<BounceIn>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch (JsonException) { b = null; }
            if (b?.Address is null || b.Type is not ("bounce" or "complaint")) return Results.Problem(statusCode: 400, title: "type (bounce or complaint) and address are required");
            await optOut.OptOutAsync(ContactChannel.Email, b.Address, b.Type == "bounce" ? "hard bounce" : "spam complaint", marketingOnly: false, ct);   // repeating it is harmless
            return Results.Accepted();
        }).AllowAnonymous().WithTags("Webhooks");

        return app;
    }
}
