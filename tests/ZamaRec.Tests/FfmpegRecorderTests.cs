using ZamaRec.Core.Recording;

namespace ZamaRec.Tests;

public class FfmpegRecorderTests
{
    [Fact]
    public void AmdTriesTheGpuRoutesBeforeSystemMemory()
    {
        var amd = FfmpegRecorder.Routes("amd");
        Assert.Equal([true, true, false], amd.Select(r => r.OnGpu));
        Assert.Equal(",scale_d3d11=format=nv12", amd[0].Filters);
        Assert.DoesNotContain(amd.Where(r => r.OnGpu), r => r.Filters.Contains("hwdownload"));
        Assert.Contains("hwdownload", amd[^1].Filters);
        // NVENC takes the captured frame as it is
        Assert.Equal(("", true), (FfmpegRecorder.Routes("nvidia")[0].Filters, FfmpegRecorder.Routes("nvidia")[0].OnGpu));
        Assert.Single(FfmpegRecorder.Routes("nvidia"));
    }

    [Theory]
    [InlineData("[h264_amf @ 00000261d0548940] [error] SubmitInput() failed with error 18", true)]
    [InlineData("[mpegts @ 000001f41e267c00] [warning] Stream 0, codec av1, is muxed as a private data stream", true)]
    [InlineData("[fatal] Error : Option not found", true)]
    [InlineData("[info] Stream mapping:", false)]
    [InlineData("[info] Press [q] to stop, [?] for help", false)]
    [InlineData("[hls @ 0000022ce29c8d80] [info] Opening 'seg_000012.ts' for writing", false)]
    public void OnlyWarningsAndErrorsAreComplaints(string line, bool complaint) => Assert.Equal(complaint, FfmpegRecorder.IsComplaint(line));

    [Fact]
    public void ProgressBlocksBecomeAFrameRateNowAndThen()
    {
        var p = new FfmpegProgress(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        string? Block(long frames, double at, long dup = 0)
        {
            string? report = null;
            foreach (var line in new[] { $"frame={frames}", "fps=59.9", "bitrate=19850.1kbits/s", $"dup_frames={dup}", "drop_frames=0", "speed=1x", "progress=continue" })
                report = p.Line(line, TimeSpan.FromSeconds(at)) ?? report;
            return report;
        }
        Assert.Null(Block(15, 0.25));
        Assert.Null(Block(300, 5));
        Assert.Equal(15 + 285, p.Frames);
        Assert.Equal("60.0 fps over the last 10 s (600 frames in all, 0 duplicated and 0 dropped by ffmpeg, speed 1x, 19850.1kbits/s)", Block(600, 10));
        Assert.Null(Block(900, 15));
        // a recorder that fell behind: 150 frames in the next 30 s
        Assert.StartsWith("5.0 fps over the last 30 s (750 frames in all, 7 duplicated", Block(750, 40, dup: 7));
        Assert.Equal("750 frames in 50 s (15.0 fps on average), 7 duplicated and 0 dropped by ffmpeg", p.Summary(TimeSpan.FromSeconds(50)));
        Assert.Null(p.Line("not a pair", TimeSpan.FromSeconds(60)));
    }
}
