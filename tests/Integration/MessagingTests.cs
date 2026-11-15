using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class MessagingTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();

    private async Task DrainAsync()
    {
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 200 && await runner.RunOnceAsync(default); i++) { }
    }

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Msg " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Id, string Email)> ContactAsync(HttpClient admin, bool marketingConsent)
    {
        var email = $"m{Uniq()}@example.com";
        object[] consents = marketingConsent ? [new { channel = "Email", purpose = "Marketing", status = "Granted", source = "web form", evidence = "Ticked 'send me news' on 2026-11-01" }] : [];
        var r = await admin.PostAsJsonAsync("/api/v1/crm/contacts", new { firstName = "Mina", lastName = Uniq(), methods = new[] { new { type = "Email", value = email } }, consents });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return ((await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid(), email);
    }

    private async Task<Guid> QueueAsync(EmailRequest req)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IMessageOutbox>().QueueEmailAsync(req)).MessageId;
    }

    private static async Task<JsonElement> MessageAsync(HttpClient admin, Guid id)
        => (await admin.GetFromJsonAsync<JsonElement>("/api/v1/messaging/messages?take=300", Json)).EnumerateArray().First(m => m.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Marketing_email_to_a_consented_contact_is_sent_with_a_working_unsubscribe_path()
    {
        var admin = await host.LoginAdminAsync();
        var (cid, email) = await ContactAsync(admin, marketingConsent: true);
        var id = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "News " + Uniq(), "Big news for you.", "t-" + Uniq(), ContactId: cid));
        await DrainAsync();

        Assert.Equal("Sent", (await MessageAsync(admin, id)).GetProperty("status").GetString());
        var sent = Assert.Single(FakeEmailProvider.To(email));
        Assert.Contains("TriSoftic Test Ltd", sent.Body);                       // sender identity and address in the footer
        Assert.Contains("https://app.test/unsubscribe/", sent.Body);
        Assert.Contains("List-Unsubscribe", sent.Headers.Keys);
        Assert.Equal("List-Unsubscribe=One-Click", sent.Headers["List-Unsubscribe-Post"]);
    }

    [Fact]
    public async Task Marketing_email_without_recorded_consent_is_blocked_and_never_sent()
    {
        var admin = await host.LoginAdminAsync();
        var (cid, email) = await ContactAsync(admin, marketingConsent: false);
        var id = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "Buy now", "Please buy.", "t-" + Uniq(), ContactId: cid));
        await DrainAsync();

        var m = await MessageAsync(admin, id);
        Assert.Equal("Blocked", m.GetProperty("status").GetString());
        Assert.Contains("No marketing consent", m.GetProperty("reason").GetString());
        Assert.Empty(FakeEmailProvider.To(email));
        await Assert.ThrowsAsync<ArgumentException>(() => QueueAsync(new EmailRequest(ContactPurpose.Marketing, "x", "y", "t-" + Uniq(), ToAddress: "someone@example.com")));   // marketing to a bare address is refused outright
    }

    [Fact]
    public async Task Consent_withdrawn_after_queuing_but_before_sending_stops_the_email()
    {
        var admin = await host.LoginAdminAsync();
        var (cid, email) = await ContactAsync(admin, marketingConsent: true);
        var id = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "Late " + Uniq(), "Hello", "t-" + Uniq(), ContactId: cid));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/crm/contacts/{cid}/consent", new { channel = "Email", purpose = "Marketing", status = "Withdrawn", source = "replied STOP" })).StatusCode);
        await DrainAsync();

        Assert.Equal("Blocked", (await MessageAsync(admin, id)).GetProperty("status").GetString());
        Assert.Empty(FakeEmailProvider.To(email));
    }

    [Fact]
    public async Task The_unsubscribe_link_stops_marketing_but_not_relationship_messages_and_cannot_be_forged()
    {
        var admin = await host.LoginAdminAsync();
        var (cid, email) = await ContactAsync(admin, marketingConsent: true);
        await DrainAsync();
        var first = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "One " + Uniq(), "First", "t-" + Uniq(), ContactId: cid));
        await DrainAsync();
        var body = FakeEmailProvider.To(email).Single().Body;
        var token = body[(body.IndexOf("/unsubscribe/", StringComparison.Ordinal) + "/unsubscribe/".Length)..].Split('\n', ' ')[0];

        var anon = host.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/public/unsubscribe/{token}x")).StatusCode);                 // tampered
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync($"/api/v1/public/unsubscribe/AAAA.BBBB", null)).StatusCode);          // forged
        var view = await anon.GetFromJsonAsync<JsonElement>($"/api/v1/public/unsubscribe/{token}", Json);
        Assert.DoesNotContain(email, view.GetProperty("address").GetString());                                                          // masked
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsync($"/api/v1/public/unsubscribe/{token}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsync($"/api/v1/public/unsubscribe/{token}", null)).StatusCode);          // idempotent

        var second = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "Two " + Uniq(), "Second", "t-" + Uniq(), ContactId: cid));
        var receipt = await QueueAsync(new EmailRequest(ContactPurpose.Transactional, "Your booking " + Uniq(), "Confirmed", "t-" + Uniq(), ContactId: cid));
        await DrainAsync();
        Assert.Equal("Blocked", (await MessageAsync(admin, second)).GetProperty("status").GetString());
        Assert.Equal("Sent", (await MessageAsync(admin, receipt)).GetProperty("status").GetString());   // unsubscribing from news does not cancel a booking confirmation
        Assert.Equal(2, FakeEmailProvider.To(email).Count);
        _ = first;
    }

    [Fact]
    public async Task Relationship_email_can_go_to_a_bare_address_until_a_bounce_blocks_it_and_the_bounce_webhook_is_signed()
    {
        var admin = await host.LoginAdminAsync();
        var address = $"visitor{Uniq()}@example.com";
        var a = await QueueAsync(new EmailRequest(ContactPurpose.Transactional, "Booked " + Uniq(), "See you then", "t-" + Uniq(), ToAddress: address));
        await DrainAsync();
        Assert.Equal("Sent", (await MessageAsync(admin, a)).GetProperty("status").GetString());

        var anon = host.NewClient();
        var body = JsonSerializer.Serialize(new { type = "bounce", address });
        var ts = Clock.UtcNow.ToUnixTimeSeconds();
        HttpRequestMessage Req(string payload, string sig) => new(HttpMethod.Post, "/api/v1/webhooks/email/ses-test") { Content = new StringContent(payload, Encoding.UTF8, "application/json"), Headers = { { "X-TE-Timestamp", ts.ToString() }, { "X-TE-Signature", sig } } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(body, WebhookSigner.Sign("wrong", ts, body)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(body + " ", WebhookSigner.Sign("ses-test-shared-secret", ts, body)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/email/nobody") { Content = new StringContent(body) })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anon.SendAsync(Req(body, WebhookSigner.Sign("ses-test-shared-secret", ts, body)))).StatusCode);

        var b = await QueueAsync(new EmailRequest(ContactPurpose.Transactional, "Again " + Uniq(), "Hello again", "t-" + Uniq(), ToAddress: address));
        await DrainAsync();
        var m = await MessageAsync(admin, b);
        Assert.Equal("Blocked", m.GetProperty("status").GetString());
        Assert.Contains("suppressed", m.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Asking_twice_with_the_same_key_sends_once()
    {
        var admin = await host.LoginAdminAsync();
        var (cid, email) = await ContactAsync(admin, marketingConsent: true);
        var key = "dedup-" + Uniq();
        var a = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "Once " + Uniq(), "Body", key, ContactId: cid));
        var b = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "Once " + Uniq(), "Body", key, ContactId: cid));
        Assert.Equal(a, b);
        await DrainAsync();
        Assert.Single(FakeEmailProvider.To(email));
    }

    [Fact]
    public async Task Without_an_email_provider_messages_fail_with_a_clear_reason_and_the_messaging_pause_holds_them()
    {
        var admin = await host.LoginAdminAsync();
        var (cid, email) = await ContactAsync(admin, marketingConsent: true);

        await admin.PutAsJsonAsync("/api/v1/governance/pause/Messaging", new { isPaused = true, reason = "test" });
        Guid held;
        try
        {
            held = await QueueAsync(new EmailRequest(ContactPurpose.Marketing, "Held " + Uniq(), "Body", "t-" + Uniq(), ContactId: cid));
            await DrainAsync();
            Assert.Equal("Queued", (await MessageAsync(admin, held)).GetProperty("status").GetString());
            Assert.Empty(FakeEmailProvider.To(email));
        }
        finally { await admin.PutAsJsonAsync("/api/v1/governance/pause/Messaging", new { isPaused = false }); }

        FakeEmailProvider.Configured = false;
        try
        {
            Clock.Advance(TimeSpan.FromMinutes(3));
            await DrainAsync();
            var m = await MessageAsync(admin, held);
            Assert.Equal("Failed", m.GetProperty("status").GetString());
            Assert.Contains("No email provider", m.GetProperty("reason").GetString());
            var status = await admin.GetFromJsonAsync<JsonElement>("/api/v1/messaging/status", Json);
            Assert.False(status.GetProperty("providerConfigured").GetBoolean());
        }
        finally { FakeEmailProvider.Configured = true; }
    }

    // ---- automation: the email step and booking reminders (3.11) ----

    private async Task<(Guid TypeId, string Key)> BookingTypeAsync(HttpClient admin, Guid pid)
    {
        var staff = (await (await admin.PostAsJsonAsync("/api/v1/users", new { email = $"rem-{Uniq()}@test.local", displayName = "Rem " + Uniq(), password = "Some-Strong-Pass-123", roles = new[] { "Sales Representative" }, allProducts = true })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var t = await (await admin.PostAsJsonAsync($"/api/v1/scheduling/products/{pid}/appointment-types", new { name = "Demo", durationMinutes = 30, slotStepMinutes = 30, minNoticeHours = 1, maxDaysAhead = 30, timeZone = "UTC", staffUserIds = new[] { staff } })).Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = t.GetProperty("id").GetGuid();
        await admin.PutAsJsonAsync($"/api/v1/scheduling/appointment-types/{id}/availability", new { rules = Enumerable.Range(0, 7).Select(d => new { dayOfWeek = d, start = "00:00", end = "24:00" }) });
        return (id, t.GetProperty("publicKey").GetString()!);
    }

    [Fact]
    public async Task A_booking_gets_a_reminder_before_it_starts_but_not_if_it_was_cancelled_in_between()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var (_, key) = await BookingTypeAsync(admin, pid);
        var wf = await admin.PostAsJsonAsync("/api/v1/automation/workflows", new
        {
            name = "Reminder " + Uniq(), trigger = "booking.created", productId = pid, enabled = true,
            definition = new
            {
                steps = new object[]
                {
                    new { kind = "waituntil", field = "start", offsetMinutes = -60 },
                    new { kind = "if", field = "status", op = "eq", value = "Confirmed" },
                    new { kind = "email", purpose = "transactional", subject = "Reminder: your demo is at {{start}}", body = "Hi {{attendeeName}}, see you soon." },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, wf.StatusCode);

        var anon = host.NewClient();
        var slots = (await anon.GetFromJsonAsync<JsonElement>($"/api/v1/public/booking/{key}/slots", Json)).EnumerateArray().Select(s => s.GetProperty("start").GetDateTimeOffset()).Where(s => s > Clock.UtcNow.AddHours(4)).ToList();
        var keepEmail = $"keep{Uniq()}@example.com"; var cancelEmail = $"cancel{Uniq()}@example.com";
        var kept = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", new { start = slots[0], name = "Keeper", email = keepEmail });
        var cancelled = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", new { start = slots[1], name = "Canceller", email = cancelEmail });
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        var cancelToken = (await cancelled.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("manageToken").GetString()!;
        await DrainAsync();
        Assert.Empty(FakeEmailProvider.To(keepEmail));                       // too early: it is waiting for the reminder time

        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync($"/api/v1/public/booking/manage/{cancelToken}/cancel", new { reason = "busy" })).StatusCode);
        Clock.Advance(slots[1] - Clock.UtcNow - TimeSpan.FromMinutes(30));  // now within the hour before both bookings
        await DrainAsync();

        var reminder = Assert.Single(FakeEmailProvider.To(keepEmail));
        Assert.Contains("Hi Keeper", reminder.Body);
        Assert.StartsWith("Reminder: your demo is at", reminder.Subject);
        Assert.Empty(FakeEmailProvider.To(cancelEmail));                     // cancelled before the reminder: the "still confirmed" check stopped it
    }

    [Fact]
    public async Task The_email_step_goes_through_the_consent_gate_for_a_leads_contact()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var wf = await admin.PostAsJsonAsync("/api/v1/automation/workflows", new
        {
            name = "Welcome " + Uniq(), trigger = "lead.created", productId = pid, enabled = true,
            definition = new { steps = new object[] { new { kind = "email", purpose = "marketing", subject = "Welcome {{contactName}}", body = "Thanks for your interest." } } },
        });
        Assert.Equal(HttpStatusCode.Created, wf.StatusCode);

        async Task<string> LeadFor(bool consent)
        {
            var (cid, email) = await ContactAsync(admin, consent);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/crm/leads", new { contactId = cid, productId = pid, source = "manual" })).StatusCode);
            return email;
        }
        var consented = await LeadFor(true); var notConsented = await LeadFor(false);
        await DrainAsync();

        Assert.Single(FakeEmailProvider.To(consented));
        Assert.Empty(FakeEmailProvider.To(notConsented));                   // the workflow ran, the gate said no
        var blocked = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/messaging/messages?status=Blocked&productId={pid}", Json)).EnumerateArray().ToList();
        Assert.Contains(blocked, m => m.GetProperty("reason").GetString()!.Contains("No marketing consent"));
    }

    [Fact]
    public async Task Email_steps_are_validated_when_a_workflow_is_saved()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        async Task<HttpStatusCode> Save(object step) => (await admin.PostAsJsonAsync("/api/v1/automation/workflows", new { name = "V " + Uniq(), trigger = "lead.created", productId = pid, definition = new { steps = new[] { step } } })).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Save(new { kind = "email", subject = "x", body = "y" }));                              // no purpose
        Assert.Equal(HttpStatusCode.BadRequest, await Save(new { kind = "email", purpose = "marketing", body = "y" }));                      // no subject
        Assert.Equal(HttpStatusCode.BadRequest, await Save(new { kind = "waituntil" }));                                                      // no field
        Assert.Equal(HttpStatusCode.Created, await Save(new { kind = "email", purpose = "transactional", subject = "x", body = "y" }));
    }
}
