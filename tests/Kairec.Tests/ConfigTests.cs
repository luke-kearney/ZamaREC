using Kairec.Core.Config;

namespace Kairec.Tests;

public class ConfigTests
{
    [Fact]
    public void DefaultConfigParses()
    {
        var cfg = ConfigLoader.Parse(DefaultConfig.Toml);
        Assert.Equal("DP-1", cfg.Capture.Monitor);
        Assert.Equal(60, cfg.Capture.Fps);
        Assert.Equal(["default_output"], cfg.Capture.Audio);
        Assert.Equal(2, cfg.Profiles.Count);

        var cs2 = cfg.Effective(cfg.FindProfile("cs2")!);
        Assert.Equal(CaptureMode.Timeline, cs2.Mode);
        Assert.Equal("cs2-gsi", cs2.Profile.Events);
        Assert.Equal(120, cs2.WindowMinutes);
        Assert.Equal(5, cs2.SegmentSeconds);
        Assert.Equal("disk", cs2.TimelineStorage);
        Assert.Equal("high", cs2.QualityName);   // via capture.quality
        Assert.Equal(20000, cs2.BitrateKbps);
        Assert.Equal(4, cfg.Quality.Count);
        Assert.Equal(30, cs2.SaveSeconds);
        Assert.Equal(100.0, cs2.MaxGb);

        var mc = cfg.Effective(cfg.FindProfile("minecraft")!);
        Assert.Equal(CaptureMode.Buffer, mc.Mode);
        Assert.Equal(30, mc.BufferMinutes);
        Assert.Equal("ram", mc.BufferStorage);
        Assert.Equal(1500, mc.MarkBeforeSeconds);
        Assert.Equal(300, mc.MarkAfterSeconds);
        Assert.Equal(30, mc.MarkClipExpireHours);
        Assert.NotNull(mc.Profile.MatchCmdline);
    }

    [Fact]
    public void RejectsBadMode()
    {
        var toml = "[[profiles]]\nname = \"x\"\nmatch = \"x\"\nmode = \"bogus\"\n";
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse(toml));
    }

    [Fact]
    public void ProfileOverridesWin()
    {
        var toml = "[buffer]\nminutes = 10\n[[profiles]]\nname = \"x\"\nmatch = \"x\"\nmode = \"buffer\"\nbuffer.minutes = 45\nbuffer.storage = \"disk\"\nclips.mark_after_seconds = 7\n";
        var e = ConfigLoader.Parse(toml).Effective(ConfigLoader.Parse(toml).Profiles[0]);
        Assert.Equal(45, e.BufferMinutes);
        Assert.Equal("disk", e.BufferStorage);
        Assert.Equal(7, e.MarkAfterSeconds);
        Assert.Equal(1500, e.MarkBeforeSeconds);
    }

    [Fact]
    public void ExpandsHome()
    {
        var cfg = ConfigLoader.Parse("[storage]\nroot = \"~/Videos/k\"\n");
        Assert.StartsWith(Paths.Home(), cfg.RootDir);
        Assert.EndsWith("Videos/k/sessions", cfg.SessionsDir);
    }
}
