using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.Modules.Content;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class ContentPublishStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static ContentItem Scheduled() { var i = new ContentItem { CurrentVersion = 1 }; i.Submit(); i.Approve(1); i.Schedule(Now.AddHours(1), Now); return i; }

    [Fact]
    public void Published_is_only_reachable_through_publishing_and_records_the_time()
    {
        var i = Scheduled();
        i.BeginPublish(1);
        Assert.Equal(ContentStatus.Publishing, i.Status);
        i.CompletePublish(1, Now.AddHours(1));
        Assert.Equal(ContentStatus.Published, i.Status);
        Assert.Equal(Now.AddHours(1), i.PublishedAt);
        Assert.Null(i.ScheduledFor);
    }

    [Fact]
    public void Content_that_is_not_scheduled_or_not_the_approved_current_version_cannot_start_publishing()
    {
        var draft = new ContentItem { CurrentVersion = 1 };
        Assert.Throws<IllegalTransitionException>(() => draft.BeginPublish(1));
        var approved = new ContentItem { CurrentVersion = 1 }; approved.Submit(); approved.Approve(1);
        Assert.Throws<IllegalTransitionException>(() => approved.BeginPublish(1));          // approved but never scheduled
        var s = Scheduled();
        Assert.Throws<IllegalTransitionException>(() => s.BeginPublish(2));                  // not the current version
        Assert.Throws<IllegalTransitionException>(() => new ContentItem { CurrentVersion = 1, Status = ContentStatus.Published }.CompletePublish(1, Now));   // nothing skips the pipeline
    }

    [Fact]
    public void A_retried_job_can_claim_again_and_a_failure_can_be_requeued_or_abandoned()
    {
        var i = Scheduled();
        i.BeginPublish(1); i.BeginPublish(1);                       // same item claimed again on a retry
        i.FailPublish(1, "boom");
        Assert.Equal(ContentStatus.PublishFailed, i.Status);
        Assert.Equal("boom", i.LastPublishError);
        i.Requeue(1, Now.AddHours(2));
        Assert.Equal(ContentStatus.Scheduled, i.Status);
        Assert.Equal(Now.AddHours(2), i.ScheduledFor);

        i.BeginPublish(1); i.FailPublish(1, "again");
        i.Abandon(1);
        Assert.Equal(ContentStatus.Approved, i.Status);
        Assert.Null(i.ScheduledFor);
        Assert.Equal(1, i.ApprovedVersion);                          // the approval itself is untouched
    }

    [Fact]
    public void A_failed_post_can_be_confirmed_after_the_fact_and_clears_the_error()
    {
        var i = Scheduled(); i.BeginPublish(1); i.FailPublish(1, "timeout");
        i.CompletePublish(1, Now.AddHours(3));                       // found on the page, or marked published by hand
        Assert.Equal(ContentStatus.Published, i.Status);
        Assert.Null(i.LastPublishError);
    }

    [Fact]
    public void Content_being_published_cannot_be_edited_and_published_content_cannot_be_cancelled()
    {
        var i = Scheduled(); i.BeginPublish(1);
        Assert.Throws<IllegalTransitionException>(() => i.AddVersion(2));
        Assert.Throws<IllegalTransitionException>(() => i.Cancel());
        i.CompletePublish(1, Now);
        Assert.Throws<IllegalTransitionException>(() => i.Cancel());
        i.AddVersion(2);                                             // a new version of live content starts a fresh draft
        Assert.Equal(ContentStatus.Draft, i.Status);
    }

    [Fact]
    public void Only_failed_content_can_be_requeued_or_abandoned()
    {
        Assert.Throws<IllegalTransitionException>(() => Scheduled().Requeue(1, Now));
        Assert.Throws<IllegalTransitionException>(() => Scheduled().Abandon(1));
    }
}
