using System.Buffers.Binary;

namespace TriSoftic.GrowthEngine.Modules.Assets;

/// <summary>
/// Reads the length and picture size of an MP4 from its own header boxes, with no outside tool. Returns null when anything is unreadable:
/// a number that cannot be trusted is reported as unknown, never as zero. The stream must be seekable; the video data itself is never read.
/// A phone video stored sideways (a 90-degree rotation in the track's matrix) is reported the way a viewer sees it.
/// </summary>
public static class Mp4Probe
{
    public sealed record Info(double DurationSeconds, int Width, int Height);

    private const int MaxHeaderBytes = 64 * 1024 * 1024;   // the 'moov' box (the index) is normally well under a few MB

    public static Info? Read(Stream s)
    {
        try { return ReadCore(s); }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or OverflowException or InvalidOperationException) { return null; }
    }

    private static Info? ReadCore(Stream s)
    {
        if (!s.CanSeek) return null;
        var len = s.Length; long pos = 0;
        Span<byte> head = stackalloc byte[16];
        while (pos + 8 <= len)
        {
            s.Position = pos;
            if (s.Read(head[..8]) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(head); var type = System.Text.Encoding.ASCII.GetString(head.Slice(4, 4)); long header = 8;
            if (size == 1) { if (s.Read(head[8..16]) < 8) return null; size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..16]); header = 16; }
            else if (size == 0) size = len - pos;
            if (size < header || pos + size > len) return null;
            if (type == "moov")
            {
                var bodyLen = size - header;
                if (bodyLen > MaxHeaderBytes) return null;
                var body = new byte[bodyLen]; s.Position = pos + header; s.ReadExactly(body);
                return ReadMoov(body);
            }
            pos += size;
        }
        return null;
    }

    private static IEnumerable<(string Type, int Start, int End)> Boxes(byte[] b, int start, int end)
    {
        var p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4)); var type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4); var header = 8;
            if (size == 1) { if (p + 16 > end) yield break; size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8, 8)); header = 16; }
            else if (size == 0) size = end - p;
            if (size < header || p + size > end) yield break;
            yield return (type, p + header, p + (int)size);
            p += (int)size;
        }
    }

    private static Info? ReadMoov(byte[] b)
    {
        double? seconds = null;
        foreach (var (type, start, end) in Boxes(b, 0, b.Length))
        {
            if (type == "mvhd")
            {
                var v = b[start];
                var timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(start + (v == 1 ? 20 : 12), 4));
                var duration = v == 1 ? (double)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(start + 24, 8)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(start + 16, 4));
                if (timescale > 0) seconds = duration / timescale;
            }
            else if (type == "trak" && seconds is not null && ReadVideoTrack(b, start, end) is { } size)
                return new Info(seconds.Value, size.W, size.H);
        }
        return null;
    }

    private static (int W, int H)? ReadVideoTrack(byte[] b, int start, int end)
    {
        (int W, int H)? dims = null; var isVideo = false;
        foreach (var (type, s, e) in Boxes(b, start, end))
        {
            if (type == "tkhd")
            {
                var v = b[s]; var matrixAt = s + (v == 1 ? 52 : 40); var sizeAt = matrixAt + 36;   // ISO 14496-12 tkhd layout; matrix is 9 x 4 bytes, then width and height as 16.16
                if (sizeAt + 8 > e) return null;
                var a = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(matrixAt, 4));
                var w = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(sizeAt, 4)) >> 16; var h = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(sizeAt + 4, 4)) >> 16;
                dims = a == 0 ? ((int)h, (int)w) : ((int)w, (int)h);   // a == 0: the picture is stored rotated a quarter turn
            }
            else if (type == "mdia")
                foreach (var (t2, s2, e2) in Boxes(b, s, e))
                    if (t2 == "hdlr" && s2 + 12 <= e2) isVideo = System.Text.Encoding.ASCII.GetString(b, s2 + 8, 4) == "vide";
        }
        return isVideo && dims is { W: > 0, H: > 0 } ? dims : null;
    }
}
