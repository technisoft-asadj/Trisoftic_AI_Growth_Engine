using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Assets;

public static class AssetsModule
{
    public static IServiceCollection AddAssetsModule(this IServiceCollection s, IConfiguration c)
    {
        s.AddModuleDbContext<AssetsDbContext>(c, AssetsDbContext.Schema);
        s.AddSingleton<IObjectStorage, FileSystemObjectStorage>();
        s.AddScoped<IAssetCatalog, AssetCatalog>();
        s.AddScoped<IGeneratedAssetStore, GeneratedAssetStore>();
        return s;
    }

    public sealed record AssetDto(Guid Id, Guid ProductId, Guid GroupId, int Version, string Name, AssetKind Kind, string ContentType, long SizeBytes, string Sha256,
        AssetSource Source, string? OwnerName, UsageRights Rights, string? RightsNotes, DateTimeOffset? RightsExpiresAt, bool IsSynthetic, bool IsRetired, DateTimeOffset CreatedAt,
        double? DurationSeconds = null, int? Width = null, int? Height = null);

    private static AssetDto ToDto(Asset a) => new(a.Id, a.ProductId, a.GroupId, a.Version, a.Name, a.Kind, a.ContentType, a.SizeBytes, a.Sha256, a.Source, a.OwnerName,
        a.Rights, a.RightsNotes, a.RightsExpiresAt, a.IsSynthetic, a.IsRetired, a.CreatedAt, a.DurationSeconds, a.Width, a.Height);

    public static IEndpointRouteBuilder MapAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Assets");

        g.MapGet("/products/{productId:guid}/assets", async (Guid productId, AssetsDbContext db, ICurrentUser u, bool? includeRetired, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AssetsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var q = db.Assets.AsNoTracking().Where(a => a.ProductId == productId);
            if (includeRetired != true) q = q.Where(a => !a.IsRetired);
            // latest version of each group
            var rows = await q.OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
            return Results.Ok(rows.GroupBy(a => a.GroupId).Select(x => x.OrderByDescending(a => a.Version).First()).Select(ToDto));
        });

        g.MapGet("/assets/{id:guid}", async (Guid id, AssetsDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            var a = await Visible(db, u, id, Permissions.AssetsRead, ct);
            if (a is null) return Results.NotFound();
            var versions = await db.Assets.AsNoTracking().Where(x => x.GroupId == a.GroupId).OrderByDescending(x => x.Version).ToListAsync(ct);
            return Results.Ok(new { asset = ToDto(a), versions = versions.Select(ToDto) });
        });

        g.MapGet("/assets/{id:guid}/content", async (Guid id, AssetsDbContext db, ICurrentUser u, IObjectStorage storage, HttpContext http, CancellationToken ct) =>
        {
            var a = await Visible(db, u, id, Permissions.AssetsRead, ct);
            if (a is null) return Results.NotFound();
            var stream = await storage.OpenReadAsync(a.StorageKey, ct);
            if (stream is null) return Results.Problem(statusCode: 410, title: "The stored file is missing");
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            return Results.File(stream, a.ContentType, a.Name, enableRangeProcessing: true);
        });

        g.MapPost("/products/{productId:guid}/assets", (Delegate)UploadNew).DisableAntiforgery();
        g.MapPost("/assets/{id:guid}/versions", (Delegate)UploadVersion).DisableAntiforgery();

        g.MapPost("/assets/{id:guid}/retire/{retired:bool}", async (Guid id, bool retired, AssetsDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            var a = await Visible(db, u, id, Permissions.AssetsWrite, ct, track: true);
            if (a is null) return Results.NotFound();
            foreach (var v in await db.Assets.Where(x => x.GroupId == a.GroupId).ToListAsync(ct)) v.IsRetired = retired;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry(retired ? "asset.retired" : "asset.restored", "Asset", a.GroupId.ToString(), a.ProductId, a.Name), ct);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<Asset?> Visible(AssetsDbContext db, ICurrentUser u, Guid id, string permission, CancellationToken ct, bool track = false)
    {
        if (!u.HasPermission(permission)) return null;
        var q = track ? db.Assets.AsQueryable() : db.Assets.AsNoTracking();
        var a = await q.FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is not null && await u.CanAccessProductAsync(a.ProductId, ct) ? a : null;
    }

    private static async Task<IResult> UploadNew(Guid productId, HttpRequest req, AssetsDbContext db, ICurrentUser u, IObjectStorage storage, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        if (!u.HasPermission(Permissions.AssetsWrite) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
        var form = await ReadForm(req, ct);
        if (form.Error is not null) return Results.Problem(statusCode: 400, title: form.Error);
        var groupId = Guid.CreateVersion7();
        var res = await Store(db, storage, clock, u, productId, groupId, 1, form, ct);
        if (res.Error is not null) return Results.Problem(statusCode: 400, title: res.Error);
        await audit.WriteAsync(new AuditEntry("asset.uploaded", "Asset", groupId.ToString(), productId, res.Asset!.Name, After: new { res.Asset.Kind, res.Asset.Source, res.Asset.Rights, res.Asset.Sha256 }), ct);
        return Results.Created($"/api/v1/assets/{res.Asset.Id}", ToDto(res.Asset));
    }

    private static async Task<IResult> UploadVersion(Guid id, HttpRequest req, AssetsDbContext db, ICurrentUser u, IObjectStorage storage, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var current = await Visible(db, u, id, Permissions.AssetsWrite, ct);
        if (current is null) return Results.NotFound();
        var latest = await db.Assets.Where(x => x.GroupId == current.GroupId).MaxAsync(x => x.Version, ct);
        var form = await ReadForm(req, ct);
        if (form.Error is not null) return Results.Problem(statusCode: 400, title: form.Error);
        var res = await Store(db, storage, clock, u, current.ProductId, current.GroupId, latest + 1, form, ct);
        if (res.Error is not null) return Results.Problem(statusCode: 400, title: res.Error);
        await audit.WriteAsync(new AuditEntry("asset.version-uploaded", "Asset", current.GroupId.ToString(), current.ProductId, res.Asset!.Name, After: new { res.Asset.Version, res.Asset.Sha256 }), ct);
        return Results.Created($"/api/v1/assets/{res.Asset.Id}", ToDto(res.Asset));
    }

    private sealed record UploadForm(IFormFile? File, AssetKind Kind, AssetSource Source, UsageRights Rights, string? Owner, string? Notes, DateTimeOffset? Expires, bool Synthetic, string? Error);

    private static async Task<UploadForm> ReadForm(HttpRequest req, CancellationToken ct)
    {
        if (!req.HasFormContentType) return new(null, default, default, default, null, null, null, false, "Expected multipart/form-data");
        var f = await req.ReadFormAsync(ct);
        var file = f.Files.FirstOrDefault();
        if (file is null) return new(null, default, default, default, null, null, null, false, "A file is required");
        if (!Enum.TryParse<AssetKind>(f["kind"], true, out var kind)) return new(file, default, default, default, null, null, null, false, "kind is required (" + string.Join(", ", Enum.GetNames<AssetKind>()) + ")");
        if (!Enum.TryParse<AssetSource>(f["source"], true, out var source)) return new(file, kind, default, default, null, null, null, false, "source is required (" + string.Join(", ", Enum.GetNames<AssetSource>()) + ")");
        if (!Enum.TryParse<UsageRights>(f["rights"], true, out var rights)) return new(file, kind, source, default, null, null, null, false, "rights is required (" + string.Join(", ", Enum.GetNames<UsageRights>()) + ")");
        DateTimeOffset? expires = DateTimeOffset.TryParse(f["rightsExpiresAt"], out var e) ? e : null;
        // AI-generated sources are always synthetic regardless of what the client says.
        var synthetic = source == AssetSource.AiGenerated || string.Equals(f["isSynthetic"], "true", StringComparison.OrdinalIgnoreCase);
        return new(file, kind, source, rights, f["owner"].ToString(), f["rightsNotes"].ToString(), expires, synthetic, null);
    }

    private static async Task<(Asset? Asset, string? Error)> Store(AssetsDbContext db, IObjectStorage storage, IClock clock, ICurrentUser u, Guid productId, Guid groupId, int version, UploadForm form, CancellationToken ct)
    {
        var file = form.File!;
        await using var input = file.OpenReadStream();
        var head = new byte[16];
        var read = await input.ReadAsync(head, ct);
        if (FileValidation.Check(file.FileName, file.Length, head.AsSpan(0, read).ToArray(), out var rule) is { } err) return (null, err);
        input.Position = 0;

        var sha = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
        input.Position = 0;
        var key = $"assets/{productId:N}/{Guid.NewGuid():N}";
        await storage.PutAsync(key, input, rule!.ContentType, ct);

        var asset = new Asset
        {
            ProductId = productId, GroupId = groupId, Version = version, Name = FileValidation.SafeName(file.FileName), Kind = form.Kind, ContentType = rule.ContentType,
            SizeBytes = file.Length, Sha256 = sha, StorageKey = key, Source = form.Source, OwnerName = form.Owner, Rights = form.Rights, RightsNotes = form.Notes,
            RightsExpiresAt = form.Expires, IsSynthetic = form.Synthetic, UploadedBy = u.UserId, CreatedAt = clock.UtcNow,
        };
        if (rule.ContentType == "video/mp4")
        {
            input.Position = 0;
            if (Mp4Probe.Read(input) is { } probed) { asset.DurationSeconds = Math.Round(probed.DurationSeconds, 2); asset.Width = probed.Width; asset.Height = probed.Height; }
        }
        db.Assets.Add(asset);
        try { await db.SaveChangesAsync(ct); }
        catch { await storage.DeleteAsync(key, CancellationToken.None); throw; }   // don't orphan the object if the row fails
        return (asset, null);
    }
}

/// <summary>Files an AI-made picture in the library. It is always synthetic, always labelled as AI-generated, and never has its rights left unknown.</summary>
public sealed class GeneratedAssetStore(AssetsDbContext db, IObjectStorage storage, IClock clock, ICurrentUser user, IAuditWriter audit) : IGeneratedAssetStore
{
    public async Task<(Guid? AssetId, string? Error)> SaveAsync(NewGeneratedAsset g, CancellationToken ct = default)
    {
        if (FileValidation.Check(g.Name, g.Bytes.Length, g.Bytes.AsSpan(0, Math.Min(16, g.Bytes.Length)).ToArray(), out var rule) is { } err) return (null, err);
        var key = $"assets/{g.ProductId:N}/{Guid.NewGuid():N}";
        await using (var ms = new MemoryStream(g.Bytes, writable: false)) await storage.PutAsync(key, ms, rule!.ContentType, ct);

        var groupId = Guid.CreateVersion7();
        var asset = new Asset
        {
            ProductId = g.ProductId, GroupId = groupId, Version = 1, Name = FileValidation.SafeName(g.Name), Kind = AssetKind.Image, ContentType = rule.ContentType,
            SizeBytes = g.Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(g.Bytes)).ToLowerInvariant(), StorageKey = key,
            Source = AssetSource.AiGenerated, OwnerName = "AI-generated", Rights = UsageRights.Unrestricted, RightsNotes = g.Provenance,
            IsSynthetic = true, UploadedBy = user.UserId, CreatedAt = clock.UtcNow,
        };
        if (PngSize(g.Bytes) is { } size) { asset.Width = size.W; asset.Height = size.H; }
        db.Assets.Add(asset);
        try { await db.SaveChangesAsync(ct); }
        catch { await storage.DeleteAsync(key, CancellationToken.None); throw; }
        await audit.WriteAsync(new AuditEntry("asset.generated", "Asset", groupId.ToString(), g.ProductId, asset.Name, After: new { asset.Source, asset.Sha256, asset.RightsNotes }), ct);
        return (asset.Id, null);
    }

    /// <summary>The picture size is in the PNG header (bytes 16 to 23). Unknown for any other format, never guessed.</summary>
    internal static (int W, int H)? PngSize(byte[] b)
    {
        if (b.Length < 24 || b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4E || b[3] != 0x47) return null;
        var w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16, 4)); var h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20, 4));
        return w > 0 && h > 0 ? (w, h) : null;
    }
}

/// <summary>The assets module's side of the media contract: facts about a file, and its bytes, for content and publishing.</summary>
public sealed class AssetCatalog(AssetsDbContext db, IObjectStorage storage) : IAssetCatalog
{
    public async Task<IReadOnlyList<AssetInfo>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new AssetInfo(a.Id, a.ProductId, a.Name, a.ContentType, a.SizeBytes, a.IsRetired, a.Rights.ToString(), a.RightsExpiresAt, a.IsSynthetic, a.DurationSeconds, a.Width, a.Height)).ToListAsync(ct);

    public async Task<IReadOnlyList<AssetInfo>> ListAsync(Guid productId, CancellationToken ct = default)
        => await db.Assets.AsNoTracking().Where(a => a.ProductId == productId && !a.IsRetired).OrderBy(a => a.Id)
            .Select(a => new AssetInfo(a.Id, a.ProductId, a.Name, a.ContentType, a.SizeBytes, a.IsRetired, a.Rights.ToString(), a.RightsExpiresAt, a.IsSynthetic, a.DurationSeconds, a.Width, a.Height)).ToListAsync(ct);

    public async Task<Stream?> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var key = await db.Assets.AsNoTracking().Where(a => a.Id == id).Select(a => a.StorageKey).FirstOrDefaultAsync(ct);
        return key is null ? null : await storage.OpenReadAsync(key, ct);
    }
}
