using System.Collections.Concurrent;
using System.Text.Json;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Tests.Integration;

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset to) => _now = to;
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

/// <summary>
/// A scriptable social network. Behaviour is chosen per post text: queue "retryable", "unknown-accepted" (the post goes live but we hear a timeout),
/// "unknown-lost" (timeout and nothing went live), "permanent" or "auth"; with nothing queued the post simply succeeds.
/// <see cref="Live"/> records what actually went live, so tests can prove no duplicate was created.
/// </summary>
public sealed class ScriptedPublisher : TriSoftic.GrowthEngine.Modules.Publishing.IChannelPublisher
{
    public static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Script = new();
    public static readonly ConcurrentDictionary<string, List<string>> Live = new();
    public static readonly ConcurrentDictionary<string, int> Calls = new();

    public static void Queue(string text, params string[] behaviours) { var q = Script.GetOrAdd(text, _ => new()); foreach (var b in behaviours) q.Enqueue(b); }
    public static int LiveCount(string text) => Live.TryGetValue(text, out var l) ? l.Count : 0;

    public string Provider => "sandbox";
    public int MaxTextLength => 400;
    public IReadOnlyCollection<string> SupportedFormats { get; } = ["Post", "Story", "Reel"];

    /// <summary>What the "network" was handed for each post (keyed by its text): the format and every file, with its bytes read back from storage.</summary>
    public static readonly ConcurrentDictionary<string, (string Format, List<(string Name, string Type, byte[] Bytes)> Files)> Sent = new();

    public async Task<TriSoftic.GrowthEngine.Modules.Publishing.PostResult> PublishAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest r, CancellationToken ct)
    {
        var files = new List<(string, string, byte[])>();
        foreach (var m in r.Media ?? [])
        {
            await using var s = await m.Open(ct); using var ms = new MemoryStream(); await s.CopyToAsync(ms, ct);
            files.Add((m.FileName, m.ContentType, ms.ToArray()));
        }
        Sent[r.Text] = (r.Format, files);
        Calls.AddOrUpdate(r.Text, 1, (_, n) => n + 1);
        var next = Script.TryGetValue(r.Text, out var q) && q.TryDequeue(out var b) ? b : "ok";
        var K = TriSoftic.GrowthEngine.Modules.Publishing.PublishErrorKind.Retryable;
        switch (next)
        {
            case "crash": throw new InvalidOperationException("boom");
            case "retryable": throw new TriSoftic.GrowthEngine.Modules.Publishing.PublishException(K, "rate limited");
            case "unknown-lost": throw new TriSoftic.GrowthEngine.Modules.Publishing.PublishException(TriSoftic.GrowthEngine.Modules.Publishing.PublishErrorKind.Unknown, "timeout");
            case "permanent": throw new TriSoftic.GrowthEngine.Modules.Publishing.PublishException(TriSoftic.GrowthEngine.Modules.Publishing.PublishErrorKind.Permanent, "text rejected by policy");
            case "auth": throw new TriSoftic.GrowthEngine.Modules.Publishing.PublishException(TriSoftic.GrowthEngine.Modules.Publishing.PublishErrorKind.AuthExpired, "token revoked");
        }
        var id = "post-" + Guid.NewGuid().ToString("N")[..10];
        Live.AddOrUpdate(r.Text, _ => [id], (_, l) => { lock (l) l.Add(id); return l; });
        if (next == "unknown-accepted") throw new TriSoftic.GrowthEngine.Modules.Publishing.PublishException(TriSoftic.GrowthEngine.Modules.Publishing.PublishErrorKind.Unknown, "timeout after the provider accepted it");
        return new TriSoftic.GrowthEngine.Modules.Publishing.PostResult(id, "https://social.test/" + id);
    }

    public Task<TriSoftic.GrowthEngine.Modules.Publishing.PostResult?> FindRecentAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest r, CancellationToken ct)
        => Task.FromResult(Live.TryGetValue(r.Text, out var l) && l.Count > 0 ? new TriSoftic.GrowthEngine.Modules.Publishing.PostResult(l[0], "https://social.test/" + l[0]) : null);

    public static volatile bool AccountsValid = true;

    public static volatile List<TriSoftic.GrowthEngine.Modules.Publishing.InsightPoint>? Insights;
    public Task<IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.InsightPoint>?> GetInsightsAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest account, DateOnly from, DateOnly to, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.InsightPoint>?>(Insights);
    public Task<bool> IsAccountValidAsync(string externalId, string accessToken, CancellationToken ct) => Task.FromResult(AccountsValid);

    /// <summary>What the "network" reports for every post; null means the adapter cannot report on posts.</summary>
    public static volatile TriSoftic.GrowthEngine.Modules.Publishing.PostMetrics? PostFigures;
    /// <summary>Comments the "network" holds per provider post id, and what was done to them. <see cref="ReplyFailure"/>: "unknown" (timeout after sending) or "permission".</summary>
    public static readonly ConcurrentDictionary<string, List<TriSoftic.GrowthEngine.Modules.Publishing.ProviderComment>> CommentsByPost = new();
    public static readonly List<(string CommentId, string Text)> Replies = [];
    public static readonly List<string> HiddenComments = [];
    public static volatile string? ReplyFailure;
    public Task<IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.ProviderComment>?> GetCommentsAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest account, string providerPostId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.ProviderComment>?>(CommentsByPost.TryGetValue(providerPostId, out var l) ? l.ToList() : []);
    public Task<string?> ReplyToCommentAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest account, string commentId, string text, CancellationToken ct)
    {
        if (ReplyFailure == "unknown") throw new TriSoftic.GrowthEngine.Modules.Publishing.PublishException(TriSoftic.GrowthEngine.Modules.Publishing.PublishErrorKind.Unknown, "timeout");
        if (ReplyFailure == "permission") throw new TriSoftic.GrowthEngine.Modules.Publishing.PublishException(TriSoftic.GrowthEngine.Modules.Publishing.PublishErrorKind.Permanent, "(#200) Requires pages_manage_engagement permission");
        lock (Replies) { Replies.Add((commentId, text)); return Task.FromResult<string?>("reply-" + Replies.Count); }
    }
    public Task<bool> SetCommentHiddenAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest account, string commentId, bool hidden, CancellationToken ct)
    { lock (HiddenComments) HiddenComments.Add(commentId); return Task.FromResult(true); }

    /// <summary>What the "network" reports about the Page itself; null means the adapter cannot report on the Page.</summary>
    public static volatile IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.PageFigureResult>? PageFigures;
    public Task<IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.PageFigureResult>?> GetPageFiguresAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest account, DateOnly from, DateOnly to, CancellationToken ct)
        => Task.FromResult(PageFigures);
    public Task<TriSoftic.GrowthEngine.Modules.Publishing.PostMetrics?> GetPostMetricsAsync(TriSoftic.GrowthEngine.Modules.Publishing.PostRequest account, string providerPostId, CancellationToken ct)
        => Task.FromResult(PostFigures);
}

/// <summary>Facebook's OAuth side, faked: one consent screen, two pages.</summary>
public sealed class FakeFacebookGraph : TriSoftic.GrowthEngine.Modules.Publishing.IFacebookGraph
{
    public bool IsConfigured => true;
    public string AuthorizeUrl(string state, bool instagram = false) => "https://facebook.test/dialog?state=" + Uri.EscapeDataString(state) + (instagram ? "&instagram=1" : "");
    public static string? InstagramUsername = "ludofight_official";
    public static string[] Scopes = ["pages_show_list", "instagram_basic", "instagram_content_publish"];
    /// <summary>A Page the user manages that the Pages list leaves out (but a direct lookup finds).</summary>
    public static bool ListIncludesHidden = false;
    public Task<TriSoftic.GrowthEngine.Modules.Publishing.LoginDiagnostics> DiagnoseAsync(string userToken, CancellationToken ct)
        => Task.FromResult(new TriSoftic.GrowthEngine.Modules.Publishing.LoginDiagnostics("999", "Test Owner", Scopes, new Dictionary<string, IReadOnlyList<string>> { ["pages_show_list"] = ["111", "222"] }));
    public Task<(TriSoftic.GrowthEngine.Modules.Publishing.PageInfo? Page, string? Error)> GetPageAsync(string pageId, string userToken, CancellationToken ct)
        => Task.FromResult<(TriSoftic.GrowthEngine.Modules.Publishing.PageInfo?, string?)>(pageId == "333" ? (new("333", "Hidden Page", "page-secret-token-333"), null) : (null, "(#100) No permission to access this Page"));
    public Task<IReadOnlyList<string>> GrantedScopesAsync(string token, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Scopes);
    public Task<TriSoftic.GrowthEngine.Modules.Publishing.InstagramAccount?> GetInstagramAccountAsync(string pageId, string pageToken, CancellationToken ct)
        => Task.FromResult<TriSoftic.GrowthEngine.Modules.Publishing.InstagramAccount?>(InstagramUsername is null ? null : new("17841400000000001", InstagramUsername));
    public Task<string> ExchangeCodeAsync(string code, CancellationToken ct) => Task.FromResult("user-token-for-" + code);
    public Task<IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.PageInfo>> ListPagesAsync(string userToken, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TriSoftic.GrowthEngine.Modules.Publishing.PageInfo>>(ListIncludesHidden
            ? [new("111", "Ludo Fight Official", "page-secret-token-111"), new("222", "Other Page", "page-secret-token-222"), new("333", "Hidden Page", "page-secret-token-333")]
            : [new("111", "Ludo Fight Official", "page-secret-token-111"), new("222", "Other Page", "page-secret-token-222")]);
}

/// <summary>Captures outgoing email instead of sending it.</summary>
public sealed class FakeEmailProvider : TriSoftic.GrowthEngine.Modules.Messaging.IEmailProvider
{
    public static readonly ConcurrentQueue<TriSoftic.GrowthEngine.Modules.Messaging.OutgoingEmail> Sent = new();
    public static volatile bool Configured = true;
    public bool IsConfigured => Configured;
    public string From => "growth@test.local";
    public Task<string> SendAsync(TriSoftic.GrowthEngine.Modules.Messaging.OutgoingEmail message, CancellationToken ct) { Sent.Enqueue(message); return Task.FromResult("msg-" + Guid.NewGuid().ToString("N")[..8]); }
    public static List<TriSoftic.GrowthEngine.Modules.Messaging.OutgoingEmail> To(string address) => Sent.Where(m => m.To.Equals(address, StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>Serves a scripted page instead of reaching the internet.</summary>
public sealed class FakePageFetcher : TriSoftic.GrowthEngine.Modules.Portfolio.IPageFetcher
{
    public static TriSoftic.GrowthEngine.Modules.Portfolio.FetchedPage? Page;
    public static string? FailWith;
    public static readonly List<string> Requested = [];
    public Task<TriSoftic.GrowthEngine.Modules.Portfolio.FetchedPage> FetchAsync(string url, CancellationToken ct)
    {
        Requested.Add(url);
        if (FailWith is not null) throw new TriSoftic.GrowthEngine.Modules.Portfolio.PageFetchException(FailWith);
        return Task.FromResult(Page ?? throw new InvalidOperationException("No page scripted"));
    }
}
