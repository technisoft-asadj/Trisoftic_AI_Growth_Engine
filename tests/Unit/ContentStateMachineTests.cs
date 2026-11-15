using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.Modules.Content;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class ContentStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static ContentItem Item() => new() { CurrentVersion = 1 };
    private static ContentItem Approved() { var i = Item(); i.Submit(); i.Approve(1); return i; }

    [Fact]
    public void Happy_path_draft_to_scheduled()
    {
        var i = Approved();
        i.Schedule(Now.AddHours(1), Now);
        Assert.Equal(ContentStatus.Scheduled, i.Status);
        Assert.Equal(Now.AddHours(1), i.ScheduledFor);
    }

    [Fact]
    public void Editing_after_approval_returns_to_draft_drops_the_schedule_and_requires_reapproval()
    {
        var i = Approved();
        i.Schedule(Now.AddHours(1), Now);
        i.AddVersion(2);
        Assert.Equal(ContentStatus.Draft, i.Status);
        Assert.Null(i.ScheduledFor);
        Assert.Equal(1, i.ApprovedVersion);   // history kept...
        i.Submit();
        Assert.Throws<IllegalTransitionException>(() => i.Schedule(Now.AddHours(1), Now));   // ...but v2 is not approved yet
    }

    [Fact]
    public void A_decision_for_an_old_version_is_refused()
    {
        var i = Item(); i.Submit(); i.AddVersion(2); i.Submit();
        Assert.Throws<IllegalTransitionException>(() => i.Approve(1));
        i.Approve(2);
        Assert.Equal(2, i.ApprovedVersion);
    }

    [Fact]
    public void Cannot_schedule_unapproved_or_in_the_past()
    {
        Assert.Throws<IllegalTransitionException>(() => Item().Schedule(Now.AddHours(1), Now));
        Assert.Throws<IllegalTransitionException>(() => Approved().Schedule(Now.AddMinutes(-1), Now));
        Assert.Throws<IllegalTransitionException>(() => Approved().Schedule(Now, Now));
    }

    [Fact]
    public void Submit_requires_draft_and_withdraw_requires_review()
    {
        var i = Item();
        Assert.Throws<IllegalTransitionException>(() => i.Withdraw());
        i.Submit();
        Assert.Throws<IllegalTransitionException>(() => i.Submit());
        i.Withdraw();
        Assert.Equal(ContentStatus.Draft, i.Status);
    }

    [Fact]
    public void Reject_and_changes_requested_never_approve()
    {
        var a = Item(); a.Submit(); a.Reject(1);
        Assert.Equal(ContentStatus.Rejected, a.Status);
        Assert.Null(a.ApprovedVersion);
        var b = Item(); b.Submit(); b.ReturnForChanges(1);
        Assert.Equal(ContentStatus.Draft, b.Status);
        Assert.Null(b.ApprovedVersion);
    }

    [Fact]
    public void Cancelled_content_is_final()
    {
        var i = Approved(); i.Cancel();
        Assert.Throws<IllegalTransitionException>(() => i.AddVersion(2));
        Assert.Throws<IllegalTransitionException>(() => i.Cancel());
        Assert.Throws<IllegalTransitionException>(() => i.Schedule(Now.AddHours(1), Now));
    }

    [Fact]
    public void Unschedule_returns_to_approved()
    {
        var i = Approved(); i.Schedule(Now.AddHours(1), Now); i.Unschedule();
        Assert.Equal(ContentStatus.Approved, i.Status);
        Assert.Null(i.ScheduledFor);
        Assert.Throws<IllegalTransitionException>(() => i.Unschedule());
    }
}
