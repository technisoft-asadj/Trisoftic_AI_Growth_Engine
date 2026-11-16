using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class MediaRulesTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static AssetInfo Img(string name = "a.png", string rights = "Unrestricted", bool retired = false, DateTimeOffset? expires = null, Guid? product = null)
        => new(Guid.NewGuid(), product ?? Product, name, "image/png", 1000, retired, rights, expires, false);
    private static AssetInfo Vid(string name = "v.mp4") => new(Guid.NewGuid(), Product, name, "video/mp4", 5_000_000, false, "Unrestricted", null, false);
    private static AssetInfo Pdf() => new(Guid.NewGuid(), Product, "x.pdf", "application/pdf", 10, false, "Unrestricted", null, false);
    private static List<string> V(string format, bool complete, params AssetInfo[] a) => MediaRules.Validate(format, a, Product, Now, complete);

    [Fact]
    public void A_text_post_a_photo_post_and_a_video_post_are_all_fine()
    {
        Assert.Empty(V("Post", true));
        Assert.Empty(V("Post", true, Img()));
        Assert.Empty(V("Post", true, Enumerable.Range(0, 10).Select(i => Img($"{i}.png")).ToArray()));
        Assert.Empty(V("Post", true, Vid()));
    }

    [Fact]
    public void A_post_cannot_mix_images_and_video_or_carry_too_many_files_or_other_types()
    {
        Assert.Contains(V("Post", true, Img(), Vid()), e => e.Contains("not both"));
        Assert.Contains(V("Post", true, Enumerable.Range(0, 11).Select(i => Img($"{i}.png")).ToArray()), e => e.Contains("at most 10"));
        Assert.Contains(V("Post", true, Vid("a.mp4"), Vid("b.mp4")), e => e.Contains("only one video"));
        Assert.Contains(V("Post", true, Pdf()), e => e.Contains("Only images and videos"));
    }

    [Fact]
    public void A_story_needs_exactly_one_image_by_the_time_it_is_submitted_but_may_start_without_one()
    {
        Assert.Empty(V("Story", true, Img()));
        Assert.Empty(V("Story", false));                                                                  // still being put together
        Assert.Contains(V("Story", true), e => e.Contains("exactly one image"));                          // submitted without one
        Assert.Contains(V("Story", false, Img(), Img("b.png")), e => e.Contains("exactly one image"));    // two is wrong even in draft
    }

    private static AssetInfo Clip(int w, int h, double seconds, string type = "video/mp4", string name = "clip.mp4")
        => new(Guid.NewGuid(), Product, name, type, 5_000_000, false, "Unrestricted", null, false, seconds, w, h);

    [Fact]
    public void A_reel_needs_exactly_one_vertical_mp4_of_a_workable_length()
    {
        Assert.Empty(V("Reel", true, Clip(1080, 1920, 30)));
        Assert.Empty(V("Reel", false));                                                                      // a draft may start empty
        Assert.Contains(V("Reel", true), e => e.Contains("exactly one video"));
        Assert.Contains(V("Reel", true, Img()), e => e.Contains("exactly one video"));
        Assert.Contains(V("Reel", true, Clip(1920, 1080, 30)), e => e.Contains("9:16"));                     // landscape
        Assert.Contains(V("Reel", true, Clip(1080, 1080, 30)), e => e.Contains("9:16"));                     // square
        Assert.Contains(V("Reel", true, Clip(360, 640, 30)), e => e.Contains("at least 540x960"));           // right shape, too small
        Assert.Contains(V("Reel", true, Clip(1080, 1920, 2)), e => e.Contains("at least 3 seconds"));
        Assert.Contains(V("Reel", true, Clip(1080, 1920, 95)), e => e.Contains("at most 90 seconds"));
        Assert.Empty(V("Reel", true, Clip(1080, 1920, 90)));                                                 // the limits themselves are fine
        Assert.Empty(V("Reel", true, Clip(1081, 1920, 30)));                                                 // a pixel of rounding is not a different shape
        Assert.Contains(V("Reel", true, Clip(1080, 1920, 30, "video/webm", "c.webm")), e => e.Contains("needs an MP4"));
    }

    [Fact]
    public void A_video_whose_size_could_not_be_read_cannot_be_a_reel_or_a_video_story_but_is_fine_in_a_plain_post()
    {
        var unknown = Vid("mystery.mp4");   // no duration or size known
        Assert.Contains(V("Reel", true, unknown), e => e.Contains("could not be read"));
        Assert.Contains(V("Story", true, unknown), e => e.Contains("could not be read"));
        Assert.Empty(V("Post", true, unknown));
    }

    [Fact]
    public void A_story_may_be_one_image_or_one_vertical_video_but_not_both_or_two()
    {
        Assert.Empty(V("Story", true, Clip(1080, 1920, 15)));
        Assert.Contains(V("Story", true, Clip(1080, 1920, 75)), e => e.Contains("at most 60 seconds"));
        Assert.Contains(V("Story", true, Clip(1920, 1080, 15)), e => e.Contains("9:16"));
        Assert.Contains(V("Story", true, Img(), Clip(1080, 1920, 15)), e => e.Contains("exactly one image or one video"));
    }

    [Fact]
    public void Other_formats_carry_no_media()
    {
        Assert.Empty(V("Blog", true));
        Assert.Contains(V("Blog", true, Img()), e => e.Contains("cannot carry"));
    }

    [Fact]
    public void Files_that_may_not_be_published_are_refused()
    {
        Assert.Contains(V("Post", true, Img("secret.png", rights: "InternalOnly")), e => e.Contains("internal-only"));
        Assert.Contains(V("Post", true, Img("old.png", retired: true)), e => e.Contains("retired"));
        Assert.Contains(V("Post", true, Img("licensed.png", rights: "LicensedWithLimits", expires: Now.AddDays(-1))), e => e.Contains("expired"));
        Assert.Empty(V("Post", true, Img("licensed.png", rights: "LicensedWithLimits", expires: Now.AddDays(30))));
        Assert.Contains(V("Post", true, Img("other.png", product: Guid.NewGuid())), e => e.Contains("another product"));
    }
}

public sealed class VersionMediaStorageTests
{
    [Fact]
    public void A_version_row_written_before_files_existed_reads_as_having_no_files_instead_of_failing()
    {
        // This is exactly what the column held for existing rows after the first migration: a placeholder object, not a list.
        Assert.Empty(new TriSoftic.GrowthEngine.Modules.Content.ContentVersion { AssetIdsJson = "{}" }.AssetIds);
        Assert.Empty(new TriSoftic.GrowthEngine.Modules.Content.ContentVersion { AssetIdsJson = "" }.AssetIds);
        var id = Guid.NewGuid();
        Assert.Equal([id], new TriSoftic.GrowthEngine.Modules.Content.ContentVersion { AssetIdsJson = TriSoftic.GrowthEngine.Modules.Content.ContentVersion.ToJson([id, id]) }.AssetIds);   // stored once
    }
}
