namespace TriSoftic.GrowthEngine.Modules.Assets;

/// <summary>Allow-list of upload types, with size caps and magic-byte verification so a renamed executable cannot pass as an image.</summary>
public static class FileValidation
{
    public sealed record Rule(string ContentType, long MaxBytes, Func<byte[], bool> Magic);

    private const long Mb = 1024 * 1024;

    private static bool Starts(byte[] b, params byte[] sig) => b.Length >= sig.Length && b.AsSpan(0, sig.Length).SequenceEqual(sig);

    public static readonly IReadOnlyDictionary<string, Rule> Allowed = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = new("image/png", 20 * Mb, b => Starts(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)),
        ["jpg"] = new("image/jpeg", 20 * Mb, b => Starts(b, 0xFF, 0xD8, 0xFF)),
        ["jpeg"] = new("image/jpeg", 20 * Mb, b => Starts(b, 0xFF, 0xD8, 0xFF)),
        ["gif"] = new("image/gif", 20 * Mb, b => Starts(b, (byte)'G', (byte)'I', (byte)'F', (byte)'8')),
        ["webp"] = new("image/webp", 20 * Mb, b => Starts(b, (byte)'R', (byte)'I', (byte)'F', (byte)'F') && b.Length > 12 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P'),
        ["pdf"] = new("application/pdf", 50 * Mb, b => Starts(b, (byte)'%', (byte)'P', (byte)'D', (byte)'F')),
        ["mp4"] = new("video/mp4", 200 * Mb, b => b.Length > 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p'),
        ["webm"] = new("video/webm", 200 * Mb, b => Starts(b, 0x1A, 0x45, 0xDF, 0xA3)),
    };

    public const long LargestAllowed = 200 * Mb;

    public static string? Check(string fileName, long length, byte[] head, out Rule? rule)
    {
        rule = null;
        var ext = Path.GetExtension(fileName).TrimStart('.');
        if (!Allowed.TryGetValue(ext, out rule)) return $"File type '.{ext}' is not allowed. Allowed: {string.Join(", ", Allowed.Keys.Distinct())}.";
        if (length <= 0) return "The file is empty.";
        if (length > rule.MaxBytes) return $"File is too large (max {rule.MaxBytes / Mb} MB for .{ext}).";
        if (!rule.Magic(head)) return "The file content does not match its extension.";
        return null;
    }

    /// <summary>Keeps only a safe display name; the stored object key never derives from it.</summary>
    public static string SafeName(string fileName)
    {
        var n = Path.GetFileName(fileName);
        var cleaned = new string(n.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_').ToArray()).Trim();
        return cleaned.Length == 0 ? "file" : cleaned.Length > 200 ? cleaned[^200..] : cleaned;
    }
}
