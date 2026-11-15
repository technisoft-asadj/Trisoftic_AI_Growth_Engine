using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace TriSoftic.GrowthEngine.Tests.Integration;

public sealed class TestHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Test-Admin-Password-123";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine").Build();

    Task IAsyncLifetime.InitializeAsync() => _pg.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _pg.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting feeds host configuration, which minimal-hosting Program.cs reads eagerly.
        builder.UseSetting("ConnectionStrings:Default", _pg.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Bootstrap:AdminEmail", AdminEmail);
        builder.UseSetting("Bootstrap:AdminPassword", AdminPassword);
    }

    public HttpClient NewClient()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "tests");
        return c;
    }

    public async Task<HttpClient> LoginAsync(string email, string password)
    {
        var c = NewClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        r.EnsureSuccessStatusCode();
        return c;
    }

    public Task<HttpClient> LoginAdminAsync() => LoginAsync(AdminEmail, AdminPassword);

    public string ConnectionString => _pg.GetConnectionString();
}

[CollectionDefinition("host")]
public sealed class HostCollection : ICollectionFixture<TestHost>;
