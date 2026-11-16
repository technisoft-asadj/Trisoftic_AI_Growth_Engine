using System.Buffers.Binary;
using TriSoftic.GrowthEngine.Modules.Assets;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class Mp4ProbeTests
{
    // ---- a tiny MP4 builder following the ISO base media layout (the video data is a few junk bytes: only the index is read) ----
    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length); System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4); body.CopyTo(b, 8);
        return b;
    }
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Zeros(int n) => new byte[n];

    private static byte[] Mvhd(uint timescale, uint duration) => Box("mvhd", Zeros(4), Zeros(8), U32(timescale), U32(duration), Zeros(80));   // version 0

    /// <param name="rotated">The picture is stored sideways and the matrix turns it a quarter turn.</param>
    private static byte[] Trak(int w, int h, string handler = "vide", bool rotated = false)
    {
        var matrix = new byte[36];
        if (rotated) { BinaryPrimitives.WriteInt32BigEndian(matrix.AsSpan(4), 0x10000); BinaryPrimitives.WriteInt32BigEndian(matrix.AsSpan(12), -0x10000); }
        else BinaryPrimitives.WriteInt32BigEndian(matrix.AsSpan(0), 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(matrix.AsSpan(32), 0x40000000);
        var tkhd = Box("tkhd", Zeros(4), Zeros(8), U32(1), Zeros(4), Zeros(4), Zeros(8), Zeros(8), matrix, U32((uint)w << 16), U32((uint)h << 16));   // version 0
        var hdlr = Box("hdlr", Zeros(4), Zeros(4), System.Text.Encoding.ASCII.GetBytes(handler), Zeros(12), Zeros(1));
        return Box("trak", tkhd, Box("mdia", hdlr));
    }

    /// <summary>ftyp, then the data, then the index last (how many phones and editors write it), or the index first ("fast start").</summary>
    private static MemoryStream Mp4(byte[] moov, bool indexFirst = false)
    {
        var ftyp = Box("ftyp", System.Text.Encoding.ASCII.GetBytes("mp42"), Zeros(4), System.Text.Encoding.ASCII.GetBytes("mp42isom"));
        var mdat = Box("mdat", Enumerable.Repeat((byte)7, 5000).ToArray());
        return new MemoryStream(indexFirst ? [.. ftyp, .. moov, .. mdat] : [.. ftyp, .. mdat, .. moov]);
    }

    [Fact]
    public void Length_and_size_are_read_from_the_index_wherever_it_sits()
    {
        var moov = Box("moov", Mvhd(1000, 12_500), Trak(1080, 1920));
        foreach (var first in new[] { false, true })
            Assert.Equal(new Mp4Probe.Info(12.5, 1080, 1920), Mp4Probe.Read(Mp4(moov, first)));
    }

    [Fact]
    public void An_audio_track_is_ignored_and_the_video_track_is_used()
        => Assert.Equal(new Mp4Probe.Info(10, 720, 1280), Mp4Probe.Read(Mp4(Box("moov", Mvhd(600, 6000), Trak(0, 0, "soun"), Trak(720, 1280)))));

    [Fact]
    public void A_phone_video_stored_sideways_is_reported_the_way_it_is_seen()
        => Assert.Equal(new Mp4Probe.Info(5, 1080, 1920), Mp4Probe.Read(Mp4(Box("moov", Mvhd(1000, 5000), Trak(1920, 1080, rotated: true)))));

    [Fact]
    public void Anything_unreadable_is_unknown_not_zero()
    {
        Assert.Null(Mp4Probe.Read(new MemoryStream([1, 2, 3])));                                                    // not a video at all
        Assert.Null(Mp4Probe.Read(new MemoryStream([])));
        Assert.Null(Mp4Probe.Read(Mp4(Box("moov", Trak(1080, 1920)))));                                              // no duration box
        Assert.Null(Mp4Probe.Read(Mp4(Box("moov", Mvhd(0, 1000), Trak(1080, 1920)))));                               // a time scale of zero would divide by zero
        Assert.Null(Mp4Probe.Read(Mp4(Box("moov", Mvhd(1000, 1000), Trak(0, 0)))));                                  // a video track with no size
        var cut = Mp4(Box("moov", Mvhd(1000, 1000), Trak(1080, 1920))).ToArray();
        Assert.Null(Mp4Probe.Read(new MemoryStream(cut[..(cut.Length - 20)])));                                      // truncated index
    }

    /// <summary>Optional: set TEST_REAL_MP4 to a real file (for example one exported from CapCut) to check the reader against it. Does nothing when unset.</summary>
    [Fact]
    public void A_real_mp4_file_is_read_when_one_is_provided()
    {
        var path = Environment.GetEnvironmentVariable("TEST_REAL_MP4");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        using var s = File.OpenRead(path);
        var info = Mp4Probe.Read(s);
        Assert.NotNull(info);
        Assert.True(info!.DurationSeconds > 0 && info.Width > 0 && info.Height > 0);
        var expected = Environment.GetEnvironmentVariable("TEST_REAL_MP4_EXPECT");   // "seconds,width,height" as measured by a browser
        if (string.IsNullOrEmpty(expected)) return;
        var p = expected.Split(',');
        var seconds = double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(info.DurationSeconds, seconds - 0.25, seconds + 0.25);
        Assert.Equal(int.Parse(p[1]), info.Width); Assert.Equal(int.Parse(p[2]), info.Height);
    }
}
