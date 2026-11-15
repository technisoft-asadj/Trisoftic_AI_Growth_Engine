using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Assets;

/// <summary>Local-disk implementation for development and single-VM deployments. An S3-compatible implementation replaces it behind IObjectStorage.</summary>
public sealed partial class FileSystemObjectStorage(IConfiguration config) : IObjectStorage
{
    private readonly string _root = Path.GetFullPath(config["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "data", "objects"));

    [GeneratedRegex(@"^[a-z0-9]+(/[a-z0-9\-]+)*$")]
    private static partial Regex SafeKey();

    private string PathFor(string key)
    {
        if (!SafeKey().IsMatch(key)) throw new ArgumentException("Invalid storage key", nameof(key));
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root, StringComparison.Ordinal)) throw new ArgumentException("Invalid storage key", nameof(key));
        return full;
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
