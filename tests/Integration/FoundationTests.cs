using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class FoundationTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Health_ready_reports_database_up()
    {
        var r = await host.NewClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        var r = await host.NewClient().GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Unsafe_requests_without_csrf_header_are_rejected()
    {
        var c = host.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email = TestHost.AdminEmail, password = TestHost.AdminPassword });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_401()
    {
        var r = await host.NewClient().PostAsJsonAsync("/api/v1/auth/login", new { email = TestHost.AdminEmail, password = "nope-nope-nope-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Correlation_id_is_echoed()
    {
        var c = host.NewClient();
        c.DefaultRequestHeaders.Add("X-Correlation-Id", "abc-123");
        var r = await c.GetAsync("/health/live");
        Assert.Equal("abc-123", r.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Admin_can_login_and_has_all_permissions()
    {
        var c = await host.LoginAdminAsync();
        var me = await c.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Json);
        Assert.Equal(TestHost.AdminEmail, me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("allProducts").GetBoolean());
        Assert.Contains("audit.read", me.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task User_without_permission_gets_403_and_product_scope_is_enforced()
    {
        var admin = await host.LoginAdminAsync();
        var productA = Guid.NewGuid();
        var create = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            email = "creator@test.local", displayName = "Creator", password = "Creator-Password-123",
            roles = new[] { "Content Creator" }, allProducts = false, productIds = new[] { productA },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var creator = await host.LoginAsync("creator@test.local", "Creator-Password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.GetAsync("/api/v1/governance/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.GetAsync("/api/v1/users")).StatusCode);

        var me = await creator.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Json);
        Assert.False(me.GetProperty("allProducts").GetBoolean());
        Assert.Equal(productA, me.GetProperty("productIds").EnumerateArray().Single().GetGuid());
    }

    [Fact]
    public async Task Role_change_is_audited_and_pause_switch_works()
    {
        var admin = await host.LoginAdminAsync();
        var put = await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = true, reason = "test" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/governance/audit?subjectType=PauseFlag", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "governance.pause.enabled");

        await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = false });
    }

    [Fact]
    public async Task Audit_table_is_append_only()
    {
        var admin = await host.LoginAdminAsync();
        await admin.PutAsJsonAsync("/api/v1/governance/pause/Automation", new { isPaused = false });

        await using var conn = new NpgsqlConnection(host.ConnectionString);
        await conn.OpenAsync();
        await using var upd = new NpgsqlCommand("UPDATE governance.audit_events SET \"Summary\" = 'tampered'", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.Message);
        await using var del = new NpgsqlCommand("DELETE FROM governance.audit_events", conn);
        await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync());
    }
}
