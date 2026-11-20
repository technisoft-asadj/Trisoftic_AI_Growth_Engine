using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Publishing;

/// <summary>
/// Reads the newest comments on each recently published post into the inbox. Comments by the Page itself are skipped. Nothing is answered here:
/// a comment only gets a reply after a person approved its text (see <see cref="CommentEndpoints"/>).
/// </summary>
public sealed class PullCommentsHandler(PublishingDbContext db, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IClock clock) : IJobHandler
{
    public const string PullJob = "publishing.pull-comments";
    public const int WindowDays = 30;
    public string Type => PullJob;
    public PauseScope? PauseScope => null;   // read-only

    public async Task HandleAsync(JobContext c, CancellationToken ct)
    {
        var now = clock.UtcNow; var since = now.AddDays(-WindowDays);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.Status == AccountStatus.Connected).ToListAsync(ct);
        TransientJobException? failure = null;
        foreach (var a in accounts)
        {
            var publisher = publishers.FirstOrDefault(p => p.Provider == a.Provider);
            if (publisher is null) continue;
            var posts = await db.Posts.AsNoTracking().Where(p => p.AccountId == a.Id && p.Source == PostSource.Provider && p.PublishedAt >= since).ToListAsync(ct);
            string? token = null;
            foreach (var post in posts)
            {
                try
                {
                    token ??= vault.Open(a.TokenCipher);
                    var fetched = await publisher.GetCommentsAsync(new PostRequest(a.ExternalId, token, ""), post.ProviderPostId, ct);
                    if (fetched is null) break;   // this adapter cannot read comments
                    var known = (await db.Comments.AsNoTracking().Where(x => x.AccountId == a.Id && x.PublishedPostId == post.Id).Select(x => x.ProviderCommentId).ToListAsync(ct)).ToHashSet();
                    foreach (var pc in fetched.Where(x => x.AuthorId != a.ExternalId && !known.Contains(x.Id)))
                        db.Comments.Add(new PostComment
                        {
                            ProductId = post.ProductId, AccountId = a.Id, PublishedPostId = post.Id, ProviderCommentId = pc.Id, AuthorName = pc.AuthorName,
                            Text = pc.Text.Length > 8000 ? pc.Text[..8000] : pc.Text, CommentedAt = pc.CreatedAt, FetchedAt = now,
                            Status = pc.IsHidden ? CommentStatus.Hidden : CommentStatus.New,
                        });
                    await db.SaveChangesAsync(ct);
                }
                catch (PublishException ex) when (ex.Kind is PublishErrorKind.Retryable or PublishErrorKind.Unknown) { failure ??= new TransientJobException($"Comments for '{a.DisplayName}': {ex.Message}", ex.RetryAfter); }
                catch (PublishException) { break; }   // permission missing (pages_read_user_content) or token bad: the token check flags the account
                catch (System.Security.Cryptography.CryptographicException) { break; }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // a parallel pull stored the same comment first: the unique index kept it once
            }
        }
        if (failure is not null) throw failure;
    }
}

public static class CommentEndpoints
{
    public sealed record TextBody(string? Text);

    private static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();

    private static object ToDto(PostComment c, PublishedPost? post) => new
    {
        c.Id, c.ProductId, status = c.Status.ToString(), c.AuthorName, c.Text, c.CommentedAt, c.Category, draft = c.Draft, c.SuggestHide,
        missingFacts = string.IsNullOrWhiteSpace(c.MissingFacts) ? Array.Empty<string>() : c.MissingFacts.Split('\n'),
        c.ReplyText, c.RepliedAt, c.HiddenAt, c.Error, postUrl = post?.Url, postId = post?.ContentItemId,
    };

    public static void Map(RouteGroupBuilder g)
    {
        static bool Allowed(ICurrentUser u) => u.HasPermission(Permissions.PublishingManage);

        g.MapGet("/products/{productId:guid}/comments", async (Guid productId, string? status, PublishingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!Allowed(u) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var q = db.Comments.AsNoTracking().Where(c => c.ProductId == productId);
            if (Enum.TryParse<CommentStatus>(status, true, out var s)) q = q.Where(c => c.Status == s);
            var rows = await q.OrderByDescending(c => c.CommentedAt).Take(100).ToListAsync(ct);
            var postIds = rows.Select(r => r.PublishedPostId).Distinct().ToList();
            var posts = await db.Posts.AsNoTracking().Where(p => postIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            return Results.Ok(rows.Select(r => ToDto(r, posts.GetValueOrDefault(r.PublishedPostId))));
        });

        g.MapPost("/products/{productId:guid}/comments/refresh", async (Guid productId, ICurrentUser u, IJobQueue jobs, IClock clock, CancellationToken ct) =>
        {
            if (!Allowed(u) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            await jobs.EnqueueAsync(new JobRequest(PullCommentsHandler.PullJob, "{}", IdempotencyKey: $"comments-now:{clock.UtcNow.ToUnixTimeSeconds() / 60}"), ct);
            return Results.Accepted();
        });

        // A live read of the newest published post's comments, so "no comments" can be told apart from "Facebook refused". Stores nothing, sends nothing.
        g.MapPost("/products/{productId:guid}/comments/check", async (Guid productId, PublishingDbContext db, ICurrentUser u, IEnumerable<IChannelPublisher> publishers, TokenVault vault, CancellationToken ct) =>
        {
            if (!Allowed(u) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var account = await db.Accounts.AsNoTracking().Where(a => a.ProductId == productId && a.Status == AccountStatus.Connected).OrderByDescending(a => a.ConnectedAt).FirstOrDefaultAsync(ct);
            if (account is null) return Results.Ok(new { ok = false, problem = "No Facebook Page is connected for this product." });
            var post = await db.Posts.AsNoTracking().Where(p => p.AccountId == account.Id && p.Source == PostSource.Provider).OrderByDescending(p => p.PublishedAt).FirstOrDefaultAsync(ct);
            if (post is null) return Results.Ok(new { ok = false, problem = "Nothing has been published to this Page yet, so there is no post to read comments from." });
            var publisher = publishers.FirstOrDefault(p => p.Provider == account.Provider);
            try
            {
                var found = publisher is null ? null : await publisher.GetCommentsAsync(new PostRequest(account.ExternalId, vault.Open(account.TokenCipher), ""), post.ProviderPostId, ct);
                return found is null ? Results.Ok(new { ok = false, problem = "This network cannot read comments from here." })
                    : Results.Ok(new { ok = true, problem = (string?)null, comments = found.Count, post = post.Url });
            }
            catch (PublishException ex) { return Results.Ok(new { ok = false, problem = ex.Message }); }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Ok(new { ok = false, problem = "The stored connection could not be read. Reconnect the Page." }); }
        });

        g.MapPost("/comments/{id:guid}/draft", async (Guid id, PublishingDbContext db, ICurrentUser u, ICommentTriage triage, IContentPublishSource content, IClock clock, CancellationToken ct) =>
        {
            var c = await Load(db, u, id, ct); if (c is null || !u.HasPermission(Permissions.AiGenerate)) return Results.NotFound();
            if (c.Status is CommentStatus.Replied or CommentStatus.Sending or CommentStatus.Hidden or CommentStatus.Ignored) return Results.Problem(statusCode: 409, title: "This comment has already been dealt with");
            return await DraftOne(db, triage, content, clock, c, ct);
        });

        // Drafts for the oldest new comments, at most 10 per click so the AI cost of one click is bounded.
        g.MapPost("/products/{productId:guid}/comments/draft-new", async (Guid productId, PublishingDbContext db, ICurrentUser u, ICommentTriage triage, IContentPublishSource content, IClock clock, CancellationToken ct) =>
        {
            if (!Allowed(u) || !await u.CanAccessProductAsync(productId, ct) || !u.HasPermission(Permissions.AiGenerate)) return Results.NotFound();
            var batch = await db.Comments.Where(c => c.ProductId == productId && c.Status == CommentStatus.New).OrderBy(c => c.CommentedAt).Take(10).ToListAsync(ct);
            var drafted = 0; string? stoppedBecause = null;
            foreach (var c in batch)
            {
                var r = await DraftOne(db, triage, content, clock, c, ct);
                if (r is IStatusCodeHttpResult { StatusCode: >= 400 and not 422 } bad) { stoppedBecause = bad is IValueHttpResult { Value: Microsoft.AspNetCore.Mvc.ProblemDetails pd } ? pd.Title : "stopped"; break; }
                drafted++;
            }
            return Results.Ok(new { considered = batch.Count, drafted, stoppedBecause });
        });

        g.MapPut("/comments/{id:guid}/draft", async (Guid id, TextBody body, PublishingDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            var c = await Load(db, u, id, ct); if (c is null) return Results.NotFound();
            if (c.Status is not (CommentStatus.New or CommentStatus.Drafted)) return Results.Problem(statusCode: 409, title: "This comment has already been dealt with");
            var text = (body.Text ?? "").Trim();
            if (text.Length > 8000) return Results.Problem(statusCode: 400, title: "A reply can be at most 8000 characters");
            c.Draft = text.Length == 0 ? null : text; c.Status = CommentStatus.Drafted; c.DraftedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(c, null));
        });

        // The approval: the person who clicks this has read the exact text below. Nothing else ever posts a reply.
        g.MapPost("/comments/{id:guid}/send", async (Guid id, TextBody body, PublishingDbContext db, ICurrentUser u, IEnumerable<IChannelPublisher> publishers, TokenVault vault,
            IPauseSwitch pause, IConfiguration cfg, IAuditWriter audit, IClock clock, CancellationToken ct) =>
        {
            var c = await Load(db, u, id, ct); if (c is null) return Results.NotFound();
            var text = (body.Text ?? "").Trim();
            if (text.Length == 0 || text.Length > 8000) return Results.Problem(statusCode: 400, title: "A reply needs 1 to 8000 characters");
            if (await pause.IsPausedAsync(PauseScope.Publishing, ct)) return Results.Problem(statusCode: 503, title: "Publishing is paused by an administrator, so replies are held");
            if (c.Status is CommentStatus.Replied or CommentStatus.Sending) return Results.Problem(statusCode: 409, title: "This comment already has a reply");
            if (c.Status is CommentStatus.Hidden or CommentStatus.Ignored) return Results.Problem(statusCode: 409, title: "This comment was hidden or ignored, so it will not be answered");

            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == c.AccountId && a.Status == AccountStatus.Connected, ct);
            var publisher = account is null ? null : publishers.FirstOrDefault(p => p.Provider == account.Provider);
            if (account is null || publisher is null) return Results.Problem(statusCode: 409, title: "The Page for this comment is not connected any more. Reconnect it in Publishing, then try again");

            // Not a bot: no identical text to different people, and a ceiling per hour.
            var now = clock.UtcNow; var norm = Norm(text);
            var recent = await db.Comments.AsNoTracking().Where(x => x.AccountId == c.AccountId && x.Status == CommentStatus.Replied && x.RepliedAt >= now.AddHours(-24)).Select(x => new { x.ReplyText, x.RepliedAt }).ToListAsync(ct);
            if (recent.Any(r => r.ReplyText is not null && Norm(r.ReplyText) == norm)) return Results.Problem(statusCode: 409, title: "The same reply was already sent to someone else in the last 24 hours. Make it specific to this comment");
            var perHour = int.TryParse(cfg["Publishing:Comments:MaxRepliesPerHour"], out var cap) ? cap : 20;
            if (recent.Count(r => r.RepliedAt >= now.AddHours(-1)) >= perHour) return Results.Problem(statusCode: 409, title: $"The limit of {perHour} replies per hour was reached. Try again later");

            // Claim the comment so a double click or two people cannot both send.
            var claimed = await db.Comments.Where(x => x.Id == id && (x.Status == CommentStatus.New || x.Status == CommentStatus.Drafted))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CommentStatus.Sending), ct);
            if (claimed == 0) return Results.Problem(statusCode: 409, title: "This comment already has a reply");
            try
            {
                var replyId = await publisher.ReplyToCommentAsync(new PostRequest(account.ExternalId, vault.Open(account.TokenCipher), ""), c.ProviderCommentId, text, ct);
                if (replyId is null) return await Release(db, c, "This network cannot post replies from here", 409, ct);
                await db.Comments.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, CommentStatus.Replied).SetProperty(x => x.ReplyText, text).SetProperty(x => x.RepliedBy, u.UserId)
                    .SetProperty(x => x.RepliedAt, now).SetProperty(x => x.ProviderReplyId, replyId).SetProperty(x => x.Error, (string?)null), CancellationToken.None);
                await audit.WriteAsync(new AuditEntry("comment.replied", "PostComment", id.ToString(), c.ProductId, c.AuthorName ?? "comment", After: new { replyId, length = text.Length }), ct);
                return Results.Ok(new { status = "Replied", replyId });
            }
            catch (PublishException ex)
            {
                var why = ex.Kind == PublishErrorKind.Unknown
                    ? "Facebook did not confirm the reply. Check the comment on Facebook before sending again, so it is not answered twice. " + ex.Message
                    : ex.Message.Contains("(#200)") || ex.Message.Contains("pages_manage_engagement") ? "Facebook refused: this connection lacks the pages_manage_engagement permission. Reconnect the Page in Publishing and accept it. " + ex.Message : ex.Message;
                return await Release(db, c, why, 502, ct);
            }
            catch (System.Security.Cryptography.CryptographicException) { return await Release(db, c, "The stored connection could not be read. Reconnect the Page", 409, ct); }
        });

        g.MapPost("/comments/{id:guid}/hide", async (Guid id, PublishingDbContext db, ICurrentUser u, IEnumerable<IChannelPublisher> publishers, TokenVault vault, IPauseSwitch pause, IAuditWriter audit, IClock clock, CancellationToken ct) =>
        {
            var c = await Load(db, u, id, ct); if (c is null) return Results.NotFound();
            if (c.Status is CommentStatus.Replied or CommentStatus.Sending or CommentStatus.Hidden) return Results.Problem(statusCode: 409, title: "This comment has already been dealt with");
            if (await pause.IsPausedAsync(PauseScope.Publishing, ct)) return Results.Problem(statusCode: 503, title: "Publishing is paused by an administrator, so this is held");
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == c.AccountId && a.Status == AccountStatus.Connected, ct);
            var publisher = account is null ? null : publishers.FirstOrDefault(p => p.Provider == account.Provider);
            if (account is null || publisher is null) return Results.Problem(statusCode: 409, title: "The Page for this comment is not connected any more. Reconnect it in Publishing, then try again");
            try
            {
                if (!await publisher.SetCommentHiddenAsync(new PostRequest(account.ExternalId, vault.Open(account.TokenCipher), ""), c.ProviderCommentId, true, ct))
                    return Results.Problem(statusCode: 409, title: "The network did not confirm that the comment was hidden");
            }
            catch (PublishException ex) { return Results.Problem(statusCode: 502, title: ex.Message.Contains("(#200)") ? "Facebook refused: this connection lacks the pages_manage_engagement permission. Reconnect the Page. " + ex.Message : ex.Message); }
            c.Status = CommentStatus.Hidden; c.HiddenBy = u.UserId; c.HiddenAt = clock.UtcNow; c.Error = null;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("comment.hidden", "PostComment", id.ToString(), c.ProductId, c.AuthorName ?? "comment", After: new { c.Category }), ct);
            return Results.Ok(ToDto(c, null));
        });

        g.MapPost("/comments/{id:guid}/ignore", async (Guid id, PublishingDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var c = await Load(db, u, id, ct); if (c is null) return Results.NotFound();
            if (c.Status is not (CommentStatus.New or CommentStatus.Drafted)) return Results.Problem(statusCode: 409, title: "This comment has already been dealt with");
            c.Status = CommentStatus.Ignored;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(c, null));
        });
    }

    private static async Task<PostComment?> Load(PublishingDbContext db, ICurrentUser u, Guid id, CancellationToken ct)
    {
        if (!u.HasPermission(Permissions.PublishingManage)) return null;
        var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is not null && await u.CanAccessProductAsync(c.ProductId, ct) ? c : null;
    }

    private static async Task<IResult> Release(PublishingDbContext db, PostComment c, string error, int status, CancellationToken ct)
    {
        await db.Comments.Where(x => x.Id == c.Id && x.Status == CommentStatus.Sending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CommentStatus.Drafted).SetProperty(x => x.Error, error.Length > 580 ? error[..580] : error), CancellationToken.None);
        return Results.Problem(statusCode: status, title: error);
    }

    private static async Task<IResult> DraftOne(PublishingDbContext db, ICommentTriage triage, IContentPublishSource content, IClock clock, PostComment c, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.PublishedPostId, ct);
        var postText = post is null ? "" : (await content.GetAsync(post.ContentItemId, post.Version, ct)) is { } pc ? pc.Title + "\n" + pc.Body : "";
        try
        {
            var res = await triage.TriageAsync(c.ProductId, postText, c.Text, ct);
            if (!res.IsSuccess)
            {
                var e = res.Error!;
                return Results.Problem(statusCode: e.Kind switch { ErrorKind.Conflict => 409, ErrorKind.Policy => 422, _ => 400 }, title: e.Message, extensions: new Dictionary<string, object?> { ["code"] = e.Code });
            }
            var t = res.Value!;
            c.Category = t.Category; c.Draft = t.Reply; c.SuggestHide = t.SuggestHide; c.DraftedAt = clock.UtcNow; c.Error = null;
            c.MissingFacts = t.Questions.Count == 0 ? null : string.Join('\n', t.Questions);
            c.Status = CommentStatus.Drafted;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(c, post));
        }
        catch (AiException ex)
        {
            var status = ex.Outcome switch { AiOutcome.BudgetBlocked => 402, AiOutcome.Paused => 503, AiOutcome.Refused => 422, _ => 502 };
            return Results.Problem(statusCode: status, title: ex.Message, extensions: new Dictionary<string, object?> { ["outcome"] = ex.Outcome.ToString() });
        }
    }
}
