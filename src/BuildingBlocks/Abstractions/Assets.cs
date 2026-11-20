namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>
/// What other modules may know about a stored file. Rights is the asset's usage-rights label (Unknown, InternalOnly, Unrestricted, LicensedWithLimits).
/// Duration, Width and Height are read from the file itself when it is uploaded (MP4 only); null means they could not be read, never "zero".
/// </summary>
public sealed record AssetInfo(Guid Id, Guid ProductId, string Name, string ContentType, long SizeBytes, bool IsRetired, string Rights, DateTimeOffset? RightsExpiresAt, bool IsSynthetic,
    double? DurationSeconds = null, int? Width = null, int? Height = null);

/// <summary>Implemented by the assets module. Lets content attach files to a post and publishing send them, without either depending on the assets module.</summary>
public interface IAssetCatalog
{
    Task<IReadOnlyList<AssetInfo>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// <summary>The product's files that have not been retired, oldest first.</summary>
    Task<IReadOnlyList<AssetInfo>> ListAsync(Guid productId, CancellationToken ct = default);
    /// <summary>Opens the stored bytes. The caller disposes the stream. Null when the file is gone.</summary>
    Task<Stream?> OpenAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Makes an AI cartoon-style picture for a post (implemented by the AI module). Throws AiException for budget, pause or provider problems.</summary>
public interface IImageMaker
{
    /// <summary>The reason a picture brief is not acceptable (people, screens, gameplay, words, logos), or null when it is fine.</summary>
    string? CheckBrief(string brief);
    Task<(Guid? AssetId, string? Error, decimal CostUsd)> MakeAsync(Guid productId, string brief, string aspectRatio, CancellationToken ct = default);
}

/// <summary>An AI-made picture to be filed in a product's library. The assets module marks it synthetic and records where it came from.</summary>
public sealed record NewGeneratedAsset(Guid ProductId, string Name, string ContentType, byte[] Bytes, string Provenance);

/// <summary>Implemented by the assets module so the AI module can file a generated picture without depending on it.</summary>
public interface IGeneratedAssetStore
{
    /// <returns>The new asset's id, or an error message when the bytes are not an acceptable picture.</returns>
    Task<(Guid? AssetId, string? Error)> SaveAsync(NewGeneratedAsset asset, CancellationToken ct = default);
}

/// <summary>
/// The rules for which files a post may carry. Shared by content (when attaching) and publishing (when sending), so the two can never disagree.
///   Post:  no media, 1-10 images, or exactly 1 video (any shape).
///   Story: exactly 1 image, or exactly 1 vertical video.
///   Reel:  exactly 1 vertical video.
///   Anything else: no media.
/// Vertical video = 9:16 (within a small tolerance), at least 540x960, and between the format's minimum and maximum length.
/// While a draft is still being put together (complete: false) a Story or Reel may have no file yet; when it is submitted and when it is sent, it must.
/// A file whose rights are InternalOnly, whose rights have expired, or which has been retired cannot go out.
/// Limits are Meta's published Reels specs (9:16, 540x960 minimum, 3-90 s). Meta publishes no length limit for video stories, so 3-60 s is a cautious assumption to confirm against the real Page.
/// </summary>
public static class MediaRules
{
    public const int MaxImages = 10;
    public const int MinWidth = 540, MinHeight = 960;
    public const double ReelMinSeconds = 3, ReelMaxSeconds = 90, StoryMinSeconds = 3, StoryMaxSeconds = 60;
    private const double Vertical = 9.0 / 16.0, Tolerance = 0.02;

    public static bool IsVideo(AssetInfo a) => a.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    public static bool IsImage(AssetInfo a) => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static List<string> Validate(string format, IReadOnlyList<AssetInfo> assets, Guid productId, DateTimeOffset now, bool complete = true)
    {
        var e = new List<string>();
        foreach (var a in assets)
        {
            if (a.ProductId != productId) e.Add($"'{a.Name}' belongs to another product.");
            if (a.IsRetired) e.Add($"'{a.Name}' has been retired and cannot be used.");
            if (a.Rights == "InternalOnly") e.Add($"'{a.Name}' is marked internal-only, so it cannot be published.");
            if (a.RightsExpiresAt is { } x && x <= now) e.Add($"The usage rights for '{a.Name}' expired on {x:yyyy-MM-dd}.");
        }
        var images = assets.Count(IsImage);
        var videos = assets.Count(IsVideo);
        var other = assets.Count - images - videos;
        if (other > 0) e.Add("Only images and videos can be attached to a post.");
        switch (format)
        {
            case "Post":
                if (images > 0 && videos > 0) e.Add("A post can carry images or a video, not both.");
                if (images > MaxImages) e.Add($"A post can carry at most {MaxImages} images.");
                if (videos > 1) e.Add("A post can carry only one video.");
                break;
            case "Story":
                if (images + videos > 1 || other > 0 || (complete && images + videos != 1)) e.Add("A story needs exactly one image or one video.");
                else foreach (var v in assets.Where(IsVideo)) e.AddRange(VideoSpec(v, "story", StoryMinSeconds, StoryMaxSeconds));
                break;
            case "Reel":
                if (images > 0 || videos > 1 || other > 0 || (complete && videos != 1)) e.Add("A reel needs exactly one video.");
                else foreach (var v in assets.Where(IsVideo)) e.AddRange(VideoSpec(v, "reel", ReelMinSeconds, ReelMaxSeconds));
                break;
            default:
                if (assets.Count > 0) e.Add($"'{format}' content cannot carry images or video.");
                break;
        }
        return e;
    }

    private static IEnumerable<string> VideoSpec(AssetInfo v, string what, double minSeconds, double maxSeconds)
    {
        if (v.DurationSeconds is null || v.Width is null || v.Height is null)
        {
            yield return $"The size and length of '{v.Name}' could not be read. A {what} needs an MP4 exported from your video editor.";
            yield break;
        }
        if (!v.ContentType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase)) yield return $"A {what} needs an MP4 file ('{v.Name}' is {v.ContentType}).";
        var ratio = (double)v.Width / v.Height.Value;
        if (Math.Abs(ratio - Vertical) > Tolerance) yield return $"'{v.Name}' is {v.Width}x{v.Height}; a {what} must be vertical 9:16 (for example 1080x1920).";
        else if (v.Width < MinWidth || v.Height < MinHeight) yield return $"'{v.Name}' is {v.Width}x{v.Height}; a {what} must be at least {MinWidth}x{MinHeight}.";
        if (v.DurationSeconds < minSeconds) yield return $"'{v.Name}' is {v.DurationSeconds:0.#} seconds; a {what} must be at least {minSeconds:0} seconds.";
        if (v.DurationSeconds > maxSeconds) yield return $"'{v.Name}' is {v.DurationSeconds:0.#} seconds; a {what} can be at most {maxSeconds:0} seconds.";
    }
}
