using System.Collections.Concurrent;
using System.Text.Json;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Tests.Integration;

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Job handler whose behaviour is driven by the payload: {"fail":"transient"|"permanent"}.</summary>
public sealed class TestJobHandler : IJobHandler
{
    public static readonly ConcurrentDictionary<string, int> Executions = new();
    public static PauseScope? Scope;

    public string Type => "test.echo";
    public PauseScope? PauseScope => Scope;

    public Task HandleAsync(JobContext ctx, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(ctx.PayloadJson);
        var key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString()! : ctx.JobId.ToString();
        Executions.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (doc.RootElement.TryGetProperty("fail", out var f))
        {
            if (f.GetString() == "transient") throw new TransientJobException("boom");
            if (f.GetString() == "permanent") throw new PermanentJobException("never");
        }
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for n8n. Calls are recorded; a workflow whose name starts with "flaky" fails the first call of each run, like a brief outage.</summary>
public sealed class FakeWebhookClient : TriSoftic.GrowthEngine.Modules.Automation.IWebhookClient
{
    public static readonly ConcurrentQueue<TriSoftic.GrowthEngine.Modules.Automation.WebhookCall> Calls = new();
    private static readonly ConcurrentDictionary<Guid, int> Attempts = new();

    public bool IsConfigured(string name) => name == "n8n-test";

    public Task SendAsync(TriSoftic.GrowthEngine.Modules.Automation.WebhookCall call, CancellationToken ct)
    {
        var n = Attempts.AddOrUpdate(call.RunId, 1, (_, c) => c + 1);
        if (call.WorkflowName.StartsWith("flaky") && n == 1) throw new TransientJobException("n8n is down");
        Calls.Enqueue(call);
        return Task.CompletedTask;
    }
}
