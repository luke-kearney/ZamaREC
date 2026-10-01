using ZamaRec.Core.Sessions;

namespace ZamaRec.Tests;

public class SegmentIndexTests
{
    [Fact]
    public void ParsesPlaylistWithMediaSequence()
    {
        var m3u8 = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #EXT-X-MEDIA-SEQUENCE:5
            #EXTINF:10.000011,
            #EXT-X-PROGRAM-DATE-TIME:2026-09-24T17:32:21.164+0200
            seg_000005.ts
            #EXTINF:9.983333,
            seg_000006.ts
            """;
        var segs = SegmentIndex.ParsePlaylist(m3u8);
        Assert.Equal(2, segs.Count);
        Assert.Equal((5, "seg_000005.ts", 10.000011), segs[0]);
        Assert.Equal((6, "seg_000006.ts", 9.983333), segs[1]);
    }

    [Fact]
    public void IndexAccumulatesAcrossWindowSlides()
    {
        var dir = Directory.CreateTempSubdirectory("zamarec-seg").FullName;
        try
        {
            var idx = new SegmentIndex(dir);
            File.WriteAllText(Path.Combine(dir, "live.m3u8"), "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:10.0,\nseg_000000.ts\n#EXTINF:10.0,\nseg_000001.ts\n");
            Assert.True(idx.Refresh());
            // window slides: segment 0 gone from the playlist, 2 appears
            File.SetLastWriteTimeUtc(Path.Combine(dir, "live.m3u8"), DateTime.UtcNow.AddSeconds(5));
            File.WriteAllText(Path.Combine(dir, "live.m3u8"), "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:1\n#EXTINF:10.0,\nseg_000001.ts\n#EXTINF:8.5,\nseg_000002.ts\n");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "live.m3u8"), DateTime.UtcNow.AddSeconds(10));
            Assert.True(idx.Refresh());
            var all = idx.All;
            Assert.Equal(3, all.Count);
            Assert.Equal(20.0, all[2].Start);
            Assert.Equal(28.5, idx.TotalSeconds);
            // only files that exist are "available"
            File.WriteAllText(Path.Combine(dir, "seg_000002.ts"), "");
            File.WriteAllText(Path.Combine(dir, "seg_000003.ts"), "");
            var avail = idx.Available();
            Assert.Equal([2, 3], avail.Select(s => s.Seq).ToArray());
            Assert.Equal(28.5, avail[1].Start);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ConcatListTests
{
    [Fact]
    public void EverySegmentGetsItsDurationExceptTheOpenTail()
    {
        var segs = new List<SegmentEntry>
        {
            new() { Seq = 0, File = "seg_000000.ts", Start = 0, Duration = 5 },
            new() { Seq = 1, File = "seg_000001.ts", Start = 5, Duration = 4.000011 },
            new() { Seq = 2, File = "seg_000002.ts", Start = 9, Duration = 1e9 },   // the tail still being written
        };
        var lines = ZamaRec.Core.Export.ClipExporter.ConcatList(segs, "/m").ToList();
        Assert.Equal(["file '/m/seg_000000.ts'", "duration 5", "file '/m/seg_000001.ts'", "duration 4", "file '/m/seg_000002.ts'"], lines);
    }
}
