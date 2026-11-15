using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class SchedulingTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Sched " + Uniq(), categoryId = cats.EnumerateArray().First().GetProperty("id").GetGuid() });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> StaffAsync(HttpClient admin)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/users", new { email = $"staff-{Uniq()}@test.local", displayName = "Staff " + Uniq(), password = "Some-Strong-Pass-123", roles = new[] { "Sales Representative" }, allProducts = true });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    /// <summary>A demo type open every day 00:00-24:00 UTC, so slots always exist in the near future.</summary>
    private async Task<(HttpClient Admin, Guid Product, Guid TypeId, string Key, Guid[] Staff)> SetupAsync(int staffCount = 1, int before = 0, int after = 0)
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var staff = new List<Guid>(); for (var i = 0; i < staffCount; i++) staff.Add(await StaffAsync(admin));
        var r = await admin.PostAsJsonAsync($"/api/v1/scheduling/products/{pid}/appointment-types", new
        {
            name = "Demo", durationMinutes = 30, bufferBeforeMinutes = before, bufferAfterMinutes = after, slotStepMinutes = 30, minNoticeHours = 1, maxDaysAhead = 30,
            timeZone = "UTC", staffUserIds = staff,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var t = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = t.GetProperty("id").GetGuid();
        var av = await admin.PutAsJsonAsync($"/api/v1/scheduling/appointment-types/{id}/availability", new
        {
            rules = Enumerable.Range(0, 7).Select(d => new { dayOfWeek = d, start = "00:00", end = "24:00" }),
        });
        Assert.Equal(HttpStatusCode.NoContent, av.StatusCode);
        return (admin, pid, id, t.GetProperty("publicKey").GetString()!, staff.ToArray());
    }

    private static async Task<List<DateTimeOffset>> PublicSlots(HttpClient c, string key)
    {
        var s = await c.GetFromJsonAsync<JsonElement>($"/api/v1/public/booking/{key}/slots", Json);
        return s.EnumerateArray().Select(x => x.GetProperty("start").GetDateTimeOffset()).ToList();
    }

    private static object Attendee(DateTimeOffset start, string? idem = null, string? @ref = null)
        => new { start, name = "Prospect " + Uniq(), email = $"p{Uniq()}@example.com", phone = "+10000000", idempotencyKey = idem, @ref };

    [Fact]
    public async Task Fifty_concurrent_bookings_of_one_slot_produce_exactly_one_booking()
    {
        var (_, _, _, key, _) = await SetupAsync(staffCount: 1);
        var anon = host.NewClient();
        var slot = (await PublicSlots(anon, key)).First();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            var c = host.NewClient();
            return (await c.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slot))).StatusCode;
        }));

        Assert.True(results.All(s => s is HttpStatusCode.Created or HttpStatusCode.Conflict), string.Join(",", results.GroupBy(s => s).Select(g => g.Key + "x" + g.Count())));
        Assert.Equal(1, results.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(49, results.Count(s => s == HttpStatusCode.Conflict));
        Assert.DoesNotContain(slot, await PublicSlots(anon, key));
    }

    [Fact]
    public async Task Concurrent_bookings_spread_across_staff_and_never_exceed_the_staff_count()
    {
        var (_, _, _, key, _) = await SetupAsync(staffCount: 3);
        var anon = host.NewClient();
        var slot = (await PublicSlots(anon, key)).First();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            (await host.NewClient().PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slot))).StatusCode));

        Assert.Equal(3, results.Count(s => s == HttpStatusCode.Created));   // one per staff member
        Assert.Equal(9, results.Count(s => s == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task The_database_itself_refuses_overlapping_blocks_for_one_staff_member()
    {
        // Bypass the application: two inserts that overlap only through their buffers must be rejected by the exclusion constraint.
        var (_, _, typeId, key, staff) = await SetupAsync(staffCount: 1, before: 15, after: 15);
        var slots = await PublicSlots(host.NewClient(), key);
        var first = slots.First(); var second = first.AddMinutes(30);   // adjacent slots: appointments touch, buffers overlap
        Assert.Equal(HttpStatusCode.Created, (await host.NewClient().PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(first))).StatusCode);

        // The app already hides the adjacent slot because of the buffers.
        Assert.DoesNotContain(second, await PublicSlots(host.NewClient(), key));
        Assert.Equal(HttpStatusCode.Conflict, (await host.NewClient().PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(second))).StatusCode);

        await using var conn = new Npgsql.NpgsqlConnection(host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO scheduling.bookings ("Id","OrganizationId","AppointmentTypeId","ProductId","StaffUserId","StartUtc","EndUtc","BlockStartUtc","BlockEndUtc","Status",
              "AttendeeName","AttendeeEmail","ManageTokenHash","CreatedAt")
            SELECT gen_random_uuid(), "OrganizationId","AppointmentTypeId","ProductId","StaffUserId", "StartUtc" + interval '30 minutes', "EndUtc" + interval '30 minutes',
              "BlockStartUtc" + interval '30 minutes', "BlockEndUtc" + interval '30 minutes', 0, 'x','x@example.com', md5(random()::text), now()
            FROM scheduling.bookings WHERE "StaffUserId" = @s LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("s", staff[0]);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23P01", ex.SqlState);   // exclusion_violation
    }

    [Fact]
    public async Task A_retried_request_with_the_same_idempotency_key_does_not_double_book()
    {
        var (admin, _, _, key, _) = await SetupAsync(staffCount: 2);
        var anon = host.NewClient();
        var slot = (await PublicSlots(anon, key)).First();
        var body = Attendee(slot, idem: "retry-" + Uniq());

        var first = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", body);
        var again = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<JsonElement>(Json); var b = await again.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(a.GetProperty("bookingId").GetGuid(), b.GetProperty("bookingId").GetGuid());
        Assert.False(b.TryGetProperty("manageToken", out _));   // the repeat never re-discloses the secret link

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/scheduling/bookings?from={Uri.EscapeDataString(slot.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(slot.AddHours(1).ToString("O"))}", Json);
        Assert.Equal(1, list.EnumerateArray().Count(x => x.GetProperty("start").GetDateTimeOffset() == slot));
    }

    [Fact]
    public async Task An_attendee_can_view_reschedule_and_cancel_using_only_their_link()
    {
        var (_, _, _, key, _) = await SetupAsync();
        var anon = host.NewClient();
        var slots = await PublicSlots(anon, key);
        var r = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slots[0]));
        var token = (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("manageToken").GetString()!;

        var view = await anon.GetFromJsonAsync<JsonElement>($"/api/v1/public/booking/manage/{token}", Json);
        Assert.Equal("Confirmed", view.GetProperty("status").GetString());

        // Move to a later slot: the old time is freed, the new time is taken, and the old link stops working as a live booking.
        var rs = await anon.PostAsJsonAsync($"/api/v1/public/booking/manage/{token}/reschedule", new { start = slots[4] });
        Assert.Equal(HttpStatusCode.OK, rs.StatusCode);
        var newToken = (await rs.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("manageToken").GetString()!;
        var after = await PublicSlots(anon, key);
        Assert.Contains(slots[0], after); Assert.DoesNotContain(slots[4], after);
        Assert.Equal("Cancelled", (await anon.GetFromJsonAsync<JsonElement>($"/api/v1/public/booking/manage/{token}", Json)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync($"/api/v1/public/booking/manage/{token}/reschedule", new { start = slots[5] })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync($"/api/v1/public/booking/manage/{newToken}/cancel", new { reason = "Clash" })).StatusCode);
        Assert.Contains(slots[4], await PublicSlots(anon, key));
    }

    [Fact]
    public async Task Rescheduling_into_a_taken_slot_fails_and_keeps_the_original_booking()
    {
        var (_, _, _, key, _) = await SetupAsync();
        var anon = host.NewClient();
        var slots = await PublicSlots(anon, key);
        var mine = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slots[0]));
        var token = (await mine.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("manageToken").GetString()!;
        Assert.Equal(HttpStatusCode.Created, (await host.NewClient().PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slots[3]))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync($"/api/v1/public/booking/manage/{token}/reschedule", new { start = slots[3] })).StatusCode);
        Assert.Equal("Confirmed", (await anon.GetFromJsonAsync<JsonElement>($"/api/v1/public/booking/manage/{token}", Json)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_a_link_minted_by_staff_attaches_a_booking_to_a_lead()
    {
        var (admin, pid, typeId, key, _) = await SetupAsync();
        var anon = host.NewClient();
        var slots = await PublicSlots(anon, key);
        var leadId = Guid.NewGuid();

        var link = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/scheduling/appointment-types/{typeId}/link?leadId={leadId}", Json);
        var good = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slots[0], @ref: link.GetProperty("ref").GetString()));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        var forged = await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slots[1], @ref: Guid.NewGuid().ToString("N") + ".AAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Equal(HttpStatusCode.Created, forged.StatusCode);   // a bad ref is ignored, not an error

        var mine = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/scheduling/bookings?leadId={leadId}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(40).ToString("O"))}", Json);
        Assert.Single(mine.EnumerateArray());
        var all = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/scheduling/bookings?productId={pid}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(40).ToString("O"))}", Json);
        Assert.Equal(2, all.EnumerateArray().Count());
    }

    [Fact]
    public async Task Public_endpoints_validate_input_hide_staff_and_unknown_pages_are_404()
    {
        var (_, _, _, key, staff) = await SetupAsync();
        var anon = host.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/public/booking/nope/slots")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/public/booking/manage/short")).StatusCode);

        var slot = (await PublicSlots(anon, key)).First();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", new { start = slot, name = "", email = "x@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", new { start = slot, name = "A", email = "not-an-email" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync($"/api/v1/public/booking/{key}/book", Attendee(slot.AddMinutes(7)))).StatusCode);   // not a real slot

        var raw = await anon.GetStringAsync($"/api/v1/public/booking/{key}/slots");
        Assert.DoesNotContain(staff[0].ToString(), raw);
    }

    [Fact]
    public async Task Setup_is_validated_and_needs_the_right_permission_and_product_scope()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var staff = await StaffAsync(admin);
        object Type(string tz, Guid[] st, int dur = 30) => new { name = "T", durationMinutes = dur, timeZone = tz, staffUserIds = st };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/scheduling/products/{pid}/appointment-types", Type("Mars/Olympus", [staff]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/scheduling/products/{pid}/appointment-types", Type("UTC", []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/scheduling/products/{pid}/appointment-types", Type("UTC", [Guid.NewGuid()]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/scheduling/products/{pid}/appointment-types", Type("UTC", [staff], dur: 2))).StatusCode);

        var (_, _, typeId, _, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/scheduling/appointment-types/{typeId}/availability",
            new { rules = new[] { new { dayOfWeek = 1, start = "10:00", end = "09:00" } } })).StatusCode);

        var email = $"viewer-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "V", password = "Some-Strong-Pass-123", roles = new[] { "Read-only Viewer" }, allProducts = true })).EnsureSuccessStatusCode();
        var viewer = await host.LoginAsync(email, "Some-Strong-Pass-123");
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/scheduling/products/{pid}/appointment-types", Type("UTC", [staff]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/scheduling/appointment-types/{typeId}/slots")).StatusCode);
    }
}
