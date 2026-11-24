using System.Text;
using Microsoft.Extensions.Configuration;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Assets;
using TriSoftic.GrowthEngine.Modules.Publishing;

namespace TriSoftic.GrowthEngine.Tests.Unit;

/// <summary>The real disk storage the server uses must accept every key the product creates. (Instagram's temporary copies used a key it refused, so every Instagram post failed.)</summary>
public sealed class FileStorageKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "storage-" + Guid.NewGuid().ToString("N"));
    private FileSystemObjectStorage Storage() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = _root }).Build());
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 24, 7, 0, 0, TimeSpan.Zero); }

    [Fact]
    public async Task Instagrams_temporary_copy_can_be_stored_served_and_deleted_on_the_real_disk_storage()
    {
        var storage = Storage(); var clock = new Clock();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Messaging:PublicBaseUrl"] = "https://app.test", ["Messaging:LinkSecret"] = "secret-for-tests" }).Build();
        var media = new PublicMedia(storage, cfg, clock);

        var stored = await media.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("jpeg-bytes")), "jpg", default);
        var uri = new Uri(stored.Url); var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var name = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
        var opened = await media.OpenAsync(name, long.Parse(q["e"]!), q["s"]!, default);
        Assert.NotNull(opened);
        using (var sr = new StreamReader(opened!.Value.Stream)) Assert.Equal("jpeg-bytes", await sr.ReadToEndAsync());
        await media.DeleteAsync(stored.Key);
        Assert.Null(await media.OpenAsync(name, long.Parse(q["e"]!), q["s"]!, default));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../b")]
    [InlineData("/absolute")]
    [InlineData(".hidden")]
    [InlineData("Upper/case")]
    [InlineData("a//b")]
    public async Task Keys_that_could_leave_the_storage_folder_are_still_refused(string key)
        => await Assert.ThrowsAsync<ArgumentException>(() => Storage().PutAsync(key, new MemoryStream([1]), "x"));

    [Theory]
    [InlineData("assets/0a1b2c3d-1111-2222-3333-444455556666")]
    [InlineData("public-tmp/3f2a9b.jpg")]
    public async Task Keys_the_product_creates_are_accepted(string key)
    {
        var s = Storage();
        await s.PutAsync(key, new MemoryStream([1, 2, 3]), "x");
        Assert.NotNull(await s.OpenReadAsync(key));
    }
}
