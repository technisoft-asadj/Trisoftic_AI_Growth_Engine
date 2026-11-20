using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Jobs;
using TriSoftic.GrowthEngine.Modules.Publishing;

namespace TriSoftic.GrowthEngine.Tests.Integration;

/// <summary>The comments inbox: reading comments, drafting a grounded reply, and the rules that keep every reply a person's decision.</summary>
[Collection("host")]
public sealed class CommentTests(TestHost host) : IDisposable
{
    private readonly DateTimeOffset _start = host.Services.GetRequiredService<FakeClock>().UtcNow;
    /// <summary>These tests move the shared fake clock forward by hours; put it back so other tests see the time they expect.</summary>
    public void Dispose() => Clock.Set(_start);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8];
    private FakeClock Clock => host.Services.GetRequiredService<FakeClock>();

    private const string Question = """{"category":"Question","reply":"Yes, it works offline and syncs when you are back online.","questions":[]}""";
    private const string Spam = """{"category":"Spam","reply":"Thanks! Visit my site.","questions":[]}""";
    private const string NeedsFact = """{"category":"Question","reply":null,"questions":["Is there an iOS version?"]}""";
    private const string Prohibited = """{"category":"Question","reply":"Yes, we are FDA approved.","questions":[]}""";

    private async Task<HttpClient> UserAsync(HttpClient admin, string role)
    {
        var email = $"cm-{Uniq()}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = role, password = "Some-Strong-Pass-123", roles = new[] { role }, allProducts = true })).EnsureSuccessStatusCode();
        return await host.LoginAsync(email, "Some-Strong-Pass-123");
    }

    private async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Pharmacy POS").GetProperty("id").GetGuid();
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/products", new { name = "Comment POS " + Uniq(), categoryId = catId })).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/products/{pid}/profile/versions", null);
        await admin.PutAsJsonAsync($"/api/v1/products/{pid}/profile/versions/1", new
        {
            content = new
            {
                summary = "Point of sale for pharmacies",
                facts = new object[] { new { id = "f-offline", category = "Feature", text = "Works offline", evidence = "Spec", verified = true } },
                claims = new object[] { new { id = "c-fda", text = "FDA approved", status = "Prohibited" } },
                markets = new[] { new { country = "PK", languages = new[] { "en" } } },
            },
        });
        (await admin.PostAsync($"/api/v1/products/{pid}/profile/versions/1/submit", null)).EnsureSuccessStatusCode();
        var approver = await UserAsync(admin, "Approver");
        var aid = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={pid}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{aid}/decision", new { decision = "Approved" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/publishing/products/{pid}/accounts/sandbox", new { channel = "facebook", name = "Comment page" })).EnsureSuccessStatusCode();
        return pid;
    }

    private async Task DrainAsync(string jobType)
    {
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest(jobType, "{}", IdempotencyKey: "cm-" + Guid.NewGuid()));
        var runner = host.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 200 && await runner.RunOnceAsync(default); i++) { }
    }

    /// <summary>A published post on the product's page, with these comments waiting on it. Returns the page's own id too, so its own comment can be included.</summary>
    private async Task<(string PostId, string PageId)> PostWithCommentsAsync(HttpClient admin, Guid pid, params (string Id, string Text, string? AuthorId)[] comments)
    {
        var approver = await UserAsync(admin, "Approver");
        var created = await admin.PostAsJsonAsync($"/api/v1/products/{pid}/content", new { title = "Offline mode", channel = "facebook", format = "Post", body = "Offline mode " + Uniq() });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/content/{id}/submit", null);
        var ap = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?status=Pending&subjectId={id}", Json)).EnumerateArray().Single().GetProperty("id").GetGuid();
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{ap}/decision", new { decision = "Approved" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/content/{id}/schedule", new { at = Clock.UtcNow.AddMinutes(30) })).EnsureSuccessStatusCode();
        for (var i = 0; i < 8; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(i == 0 ? 31 : 45));
            await DrainAsync("publishing.dispatch");
            if ((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/content/{id}", Json)).GetProperty("item").GetProperty("status").GetString() == "Published") break;
        }
        var postId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/posts", Json)).EnumerateArray().First().GetProperty("providerPostId").GetString()!;
        var pageId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/accounts", Json)).EnumerateArray().First().GetProperty("externalId").GetString()!;
        ScriptedPublisher.CommentsByPost[postId] = comments.Select(c => new ProviderComment(c.Id, c.AuthorId ?? "user-" + c.Id, "Sara", c.Text, Clock.UtcNow, false)).ToList();
        return (postId, pageId);
    }

    private static void Script(params string[] responses) { FakeAiProvider.Script.Clear(); foreach (var r in responses) FakeAiProvider.Script.Enqueue(r); FakeAiProvider.OutputTokens = 300; }
    private static async Task<JsonElement> ListAsync(HttpClient c, Guid pid, string? status = null) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/publishing/products/{pid}/comments" + (status is null ? "" : "?status=" + status), Json);
    private static JsonElement ByText(JsonElement list, string text) => list.EnumerateArray().First(c => c.GetProperty("text").GetString() == text);
    private static int RepliesTo(string commentId) { lock (ScriptedPublisher.Replies) return ScriptedPublisher.Replies.Count(r => r.CommentId == commentId); }

    [Fact]
    public async Task Pulling_comments_stores_each_once_and_skips_the_pages_own()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var (postId, pageId) = await PostWithCommentsAsync(admin, pid, ("c1", "Does it work offline?", null), ("c2", "Thanks for the update", null));
        ScriptedPublisher.CommentsByPost[postId].Add(new ProviderComment("c3", pageId, "Our Page", "Our own reply", Clock.UtcNow, false));

        await DrainAsync("publishing.pull-comments"); await DrainAsync("publishing.pull-comments");   // twice: still once each
        var list = await ListAsync(admin, pid);
        Assert.Equal(2, list.GetArrayLength());
        Assert.All(list.EnumerateArray(), c => Assert.Equal("New", c.GetProperty("status").GetString()));
        Assert.DoesNotContain(list.EnumerateArray(), c => c.GetProperty("text").GetString() == "Our own reply");
    }

    [Fact]
    public async Task The_connection_check_tells_no_comments_apart_from_a_refusal_and_stores_nothing()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        var none = await (await admin.PostAsync($"/api/v1/publishing/products/{pid}/comments/check", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(none.GetProperty("ok").GetBoolean()); Assert.Contains("Nothing has been published", none.GetProperty("problem").GetString());
        await PostWithCommentsAsync(admin, pid, ("k1", "Hello?", null), ("k2", "Hi!", null));
        var ok = await (await admin.PostAsync($"/api/v1/publishing/products/{pid}/comments/check", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(ok.GetProperty("ok").GetBoolean()); Assert.Equal(2, ok.GetProperty("comments").GetInt32());
        Assert.Equal(0, (await ListAsync(admin, pid)).GetArrayLength());   // a check only looks: nothing is stored until a pull
        Assert.Equal(HttpStatusCode.NotFound, (await (await UserAsync(admin, "Read-only Viewer")).PostAsync($"/api/v1/publishing/products/{pid}/comments/check", null)).StatusCode);
    }

    [Fact]
    public async Task A_reply_is_drafted_from_verified_facts_spam_gets_no_reply_and_a_missing_fact_is_a_question_not_a_guess()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        await PostWithCommentsAsync(admin, pid, ("d1", "Does it work offline?", null), ("d2", "Buy cheap watches www.x.example", null), ("d3", "Is there an iOS version?", null));
        await DrainAsync("publishing.pull-comments");
        var list = await ListAsync(admin, pid);

        Script(Question);
        var q = ByText(list, "Does it work offline?"); FakeAiProvider.Prompts.Clear();
        var drafted = await admin.PostAsync($"/api/v1/publishing/comments/{q.GetProperty("id").GetGuid()}/draft", null);
        Assert.Equal(HttpStatusCode.OK, drafted.StatusCode);
        var body = await drafted.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Question", body.GetProperty("category").GetString()); Assert.Contains("works offline", body.GetProperty("draft").GetString());
        Assert.Equal("Drafted", body.GetProperty("status").GetString());
        Assert.Contains("UNTRUSTED TEXT", FakeAiProvider.Prompts.Single());              // the comment is fenced as data
        Assert.Contains("f-offline", FakeAiProvider.Prompts.Single());                    // only the verified facts are offered

        Script(Spam);
        var spam = await (await admin.PostAsync($"/api/v1/publishing/comments/{ByText(list, "Buy cheap watches www.x.example").GetProperty("id").GetGuid()}/draft", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Spam", spam.GetProperty("category").GetString()); Assert.True(spam.GetProperty("suggestHide").GetBoolean());
        Assert.Equal(JsonValueKind.Null, spam.GetProperty("draft").ValueKind);            // never a reply for spam

        Script(NeedsFact);
        var ios = await (await admin.PostAsync($"/api/v1/publishing/comments/{ByText(list, "Is there an iOS version?").GetProperty("id").GetGuid()}/draft", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(JsonValueKind.Null, ios.GetProperty("draft").ValueKind);
        Assert.Equal("Is there an iOS version?", ios.GetProperty("missingFacts")[0].GetString());
    }

    [Fact]
    public async Task A_reply_that_uses_a_prohibited_claim_is_refused_and_nothing_is_saved()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        await PostWithCommentsAsync(admin, pid, ("p1", "Is it certified?", null));
        await DrainAsync("publishing.pull-comments");
        Script(Prohibited, Prohibited);                                                    // the retry repeats the mistake
        var c = ByText(await ListAsync(admin, pid), "Is it certified?");
        var res = await admin.PostAsync($"/api/v1/publishing/comments/{c.GetProperty("id").GetGuid()}/draft", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var after = ByText(await ListAsync(admin, pid), "Is it certified?");
        Assert.Equal("New", after.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, after.GetProperty("draft").ValueKind);
    }

    [Fact]
    public async Task Only_a_person_sending_the_exact_text_posts_a_reply_and_only_once()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        await PostWithCommentsAsync(admin, pid, ("s1", "Does it work offline?", null), ("s2", "Does it sync?", null));
        await DrainAsync("publishing.pull-comments");
        var list = await ListAsync(admin, pid); var s1 = ByText(list, "Does it work offline?").GetProperty("id").GetGuid(); var s2 = ByText(list, "Does it sync?").GetProperty("id").GetGuid();
        Script(Question);
        await admin.PostAsync($"/api/v1/publishing/comments/{s1}/draft", null);
        Assert.Equal(0, RepliesTo("s1"));                                                  // drafting sends nothing

        var edited = "Yes! It works offline and syncs as soon as you reconnect.";          // the person edited the draft
        var sent = await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{s1}/send", new { text = edited });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        lock (ScriptedPublisher.Replies) Assert.Contains(("s1", edited), ScriptedPublisher.Replies);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{s1}/send", new { text = "Another try" })).StatusCode);
        Assert.Equal(1, RepliesTo("s1"));                                                  // one reply per comment
        var after = ByText(await ListAsync(admin, pid), "Does it work offline?");
        Assert.Equal("Replied", after.GetProperty("status").GetString()); Assert.Equal(edited, after.GetProperty("replyText").GetString());

        // the same text to someone else is canned text, not a conversation
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{s2}/send", new { text = "  YES! it works offline and syncs as soon as you   reconnect. " })).StatusCode);
        Assert.Equal(0, RepliesTo("s2"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{s2}/send", new { text = "Yes, it syncs automatically." })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{s2}/send", new { text = "  " })).StatusCode);
    }

    [Fact]
    public async Task A_failed_send_can_be_retried_without_a_second_reply_and_an_uncertain_one_says_to_check_first()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        await PostWithCommentsAsync(admin, pid, ("f1", "Does it work offline?", null));
        await DrainAsync("publishing.pull-comments");
        var f1 = ByText(await ListAsync(admin, pid), "Does it work offline?").GetProperty("id").GetGuid();
        try
        {
            ScriptedPublisher.ReplyFailure = "unknown";
            var r = await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{f1}/send", new { text = "Yes, it works offline." });
            Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
            Assert.Contains("Check the comment on Facebook", await r.Content.ReadAsStringAsync());
            var c = ByText(await ListAsync(admin, pid), "Does it work offline?");
            Assert.Equal("Drafted", c.GetProperty("status").GetString()); Assert.Contains("Check the comment", c.GetProperty("error").GetString());

            ScriptedPublisher.ReplyFailure = "permission";
            var perm = await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{f1}/send", new { text = "Yes, it works offline." });
            Assert.Contains("pages_manage_engagement", await perm.Content.ReadAsStringAsync());   // tells the owner to reconnect with the new permission
        }
        finally { ScriptedPublisher.ReplyFailure = null; }
        Assert.Equal(0, RepliesTo("f1"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{f1}/send", new { text = "Yes, it works offline." })).StatusCode);
        Assert.Equal(1, RepliesTo("f1"));
    }

    [Fact]
    public async Task Hiding_needs_an_explicit_confirmation_and_a_hidden_comment_is_never_answered()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        await PostWithCommentsAsync(admin, pid, ("h1", "Buy cheap watches", null), ("h2", "Idiots", null));
        await DrainAsync("publishing.pull-comments");
        var list = await ListAsync(admin, pid); var h1 = ByText(list, "Buy cheap watches").GetProperty("id").GetGuid(); var h2 = ByText(list, "Idiots").GetProperty("id").GetGuid();
        Script(Spam);
        await admin.PostAsync($"/api/v1/publishing/comments/{h1}/draft", null);
        lock (ScriptedPublisher.HiddenComments) Assert.DoesNotContain("h1", ScriptedPublisher.HiddenComments);   // a suggestion hides nothing

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/publishing/comments/{h1}/hide", null)).StatusCode);
        lock (ScriptedPublisher.HiddenComments) Assert.Contains("h1", ScriptedPublisher.HiddenComments);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{h1}/send", new { text = "Thanks" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/publishing/comments/{h2}/ignore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{h2}/send", new { text = "Thanks" })).StatusCode);
    }

    [Fact]
    public async Task Replies_need_the_right_permission_stop_when_publishing_is_paused_and_have_an_hourly_ceiling()
    {
        var admin = await host.LoginAdminAsync(); var pid = await ProductAsync(admin);
        await PostWithCommentsAsync(admin, pid, Enumerable.Range(1, 7).Select(i => ($"r{i}", $"Question number {i}?", (string?)null)).ToArray());
        await DrainAsync("publishing.pull-comments");
        var list = await ListAsync(admin, pid);
        Guid Id(int i) => ByText(list, $"Question number {i}?").GetProperty("id").GetGuid();

        var viewer = await UserAsync(admin, "Read-only Viewer"); var creator = await UserAsync(admin, "Content Creator");
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync($"/api/v1/publishing/comments/{Id(1)}/send", new { text = "Hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await creator.PostAsJsonAsync($"/api/v1/publishing/comments/{Id(1)}/send", new { text = "Hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/publishing/products/{pid}/comments")).StatusCode);

        await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = true });
        try { Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{Id(1)}/send", new { text = "Hi there" })).StatusCode); }
        finally { await admin.PutAsJsonAsync("/api/v1/governance/pause/Publishing", new { isPaused = false }); }
        Assert.Equal(0, RepliesTo("r1"));

        for (var i = 1; i <= 5; i++) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{Id(i)}/send", new { text = $"Reply number {i}, thanks for asking." })).StatusCode);
        var over = await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{Id(6)}/send", new { text = "Reply number 6, thanks for asking." });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode); Assert.Contains("per hour", await over.Content.ReadAsStringAsync());
        Assert.Equal(0, RepliesTo("r6"));
        Clock.Advance(TimeSpan.FromHours(2));                                              // the ceiling is per hour, not forever
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/publishing/comments/{Id(6)}/send", new { text = "Reply number 6, thanks for asking." })).StatusCode);
    }
}
