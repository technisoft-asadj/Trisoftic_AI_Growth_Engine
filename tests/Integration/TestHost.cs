using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Ai;

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
        builder.UseSetting("ConnectionStrings:Default", _pg.GetConnectionString() + ";Maximum Pool Size=40");   // bursts queue for a connection instead of exhausting the server
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Storage:Root", Path.Combine(Path.GetTempPath(), "te-test-objects-" + Guid.NewGuid().ToString("N")));
        builder.UseSetting("Scheduling:LinkSecret", "test-link-secret-0123456789abcdef");
        builder.UseSetting("Publishing:AllowSandbox", "true");
        builder.UseSetting("Automation:Webhooks:n8n-test:Url", "http://localhost:1/never-called");
        builder.UseSetting("Automation:Webhooks:n8n-test:Secret", "n8n-test-shared-secret");
        builder.UseSetting("Bootstrap:AdminEmail", AdminEmail);
        builder.UseSetting("Bootstrap:AdminPassword", AdminPassword);

        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IClock>();
            s.AddSingleton<FakeClock>();
            s.AddSingleton<IClock>(sp => sp.GetRequiredService<FakeClock>());
            s.AddScoped<IJobHandler, TestJobHandler>();
            s.RemoveAll<TriSoftic.GrowthEngine.Modules.Publishing.IChannelPublisher>();
            s.AddScoped<TriSoftic.GrowthEngine.Modules.Publishing.IChannelPublisher, ScriptedPublisher>();
            s.RemoveAll<TriSoftic.GrowthEngine.Modules.Publishing.IFacebookGraph>();
            s.AddScoped<TriSoftic.GrowthEngine.Modules.Publishing.IFacebookGraph, FakeFacebookGraph>();
            s.RemoveAll<TriSoftic.GrowthEngine.Modules.Automation.IWebhookClient>();
            s.AddScoped<TriSoftic.GrowthEngine.Modules.Automation.IWebhookClient, FakeWebhookClient>();
            s.RemoveAll<IAiProvider>();
            s.AddSingleton<IAiProvider, FakeAiProvider>();
        });
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
