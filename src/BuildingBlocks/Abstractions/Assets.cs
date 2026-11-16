namespace TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

/// <summary>What other modules may know about a stored file. Rights is the asset's usage-rights label (Unknown, InternalOnly, Unrestricted, LicensedWithLimits).</summary>
public sealed record AssetInfo(Guid Id, Guid ProductId, string Name, string ContentType, long SizeBytes, bool IsRetired, string Rights, DateTimeOffset? RightsExpiresAt, bool IsSynthetic);

/// <summary>Implemented by the assets module. Lets content attach files to a post and publishing send them, without either depending on the assets module.</summary>
public interface IAssetCatalog
{
    Task<IReadOnlyList<AssetInfo>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// <summary>Opens the stored bytes. The caller disposes the stream. Null when the file is gone.</summary>
    Task<Stream?> OpenAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The rules for which files a post may carry. Shared by content (when attaching) and publishing (when sending), so the two can never disagree.
/// Post: no media, 1-10 images, or exactly 1 video. Story: exactly 1 image (video stories are not supported yet). Anything else: no media.
/// While a draft is still being put together (complete: false) a story may have no image yet; when it is submitted and when it is sent, it must.
/// A file whose rights are InternalOnly, whose rights have expired, or which has been retired cannot go out.
/// </summary>
public static class MediaRules
{
    public const int MaxImages = 10;

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
        var images = assets.Count(a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        var videos = assets.Count(a => a.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
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
                if (videos > 0) e.Add("Video stories are not supported yet; use one image.");
                else if (images > 1 || other > 0 || (complete && images != 1)) e.Add("A story needs exactly one image.");
                break;
            default:
                if (assets.Count > 0) e.Add($"'{format}' content cannot carry images or video.");
                break;
        }
        return e;
    }
}
