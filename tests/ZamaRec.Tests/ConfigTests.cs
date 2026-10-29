using ZamaRec.Core.Config;

namespace ZamaRec.Tests;

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
    public void EventClipsFollowTheGameMode()
    {
        var every = ConfigLoader.Parse(DefaultConfig.Toml);
        var e = every.Effective(every.FindProfile("cs2")!);
        Assert.Empty(e.EventClipModes);
        Assert.True(e.EventWantsClip("mvp", 0, "deathmatch"));

        var toml = "[clips]\nevent_clip_modes = [\"competitive\", \"premier\", \"rush\"]\n[[profiles]]\nname = \"x\"\nmatch = \"x\"\n[[profiles]]\nname = \"y\"\nmatch = \"y\"\nclips.event_clip_modes = []\n";
        var cfg = ConfigLoader.Parse(toml);
        var x = cfg.Effective(cfg.Profiles[0]);
        Assert.True(x.EventWantsClip("kill", 3, "competitive"));
        Assert.True(x.EventWantsClip("kill", 2, "Rush"));
        Assert.False(x.EventWantsClip("kill", 3, "deathmatch"));
        Assert.False(x.EventWantsClip("kill", 3, "casual"));
        Assert.False(x.EventWantsClip("kill", 1, "competitive"));   // event_clips still decide
        Assert.True(x.EventWantsClip("mvp", 0, null));              // mode not known: not held back
        Assert.True(cfg.Effective(cfg.Profiles[1]).EventWantsClip("mvp", 0, "deathmatch"));   // profile override
        Assert.Equal(["competitive", "premier", "rush"], ConfigLoader.Parse(ConfigLoader.Serialize(cfg)).Clips.EventClipModes);   // saved by the settings page
    }

    [Fact]
    public void RejectsAppAndAppInverseOnOneTrack()
    {
        var bad = "[capture]\nmultitrack = true\n[[capture.tracks]]\nname = \"game\"\nsources = [\"app-inverse:WEBRTC VoiceEngine\", \"app:Cider\"]\n";
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse(bad));
        Assert.Contains("'game'", ex.Message);
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[capture]\naudio = [\"app:Cider\", \"app-inverse:Cider\"]\n"));
        ConfigLoader.Parse(bad.Replace("app:Cider", "app-inverse:Cider"));   // all inverse is fine
        ConfigLoader.Parse(bad.Replace("app-inverse:WEBRTC VoiceEngine", "app:WEBRTC VoiceEngine"));   // all app: too
    }

    [Fact]
    public void ExpandsHome()
    {
        var cfg = ConfigLoader.Parse("[storage]\nroot = \"~/Videos/k\"\n");
        Assert.StartsWith(Paths.Home(), cfg.RootDir);
        Assert.EndsWith("Videos/k/sessions", cfg.SessionsDir);
    }
}

public class GamePresetTests
{
    [Xunit.Fact]
    public void Profile_resolves_by_game_key_then_by_name()
    {
        Xunit.Assert.Equal("cs2", ZamaRec.Core.Config.GamePresets.Resolve(new ZamaRec.Core.Config.ProfileConfig { Name = "anything", Game = "cs2" })!.Id);
        Xunit.Assert.Equal("cs2", ZamaRec.Core.Config.GamePresets.Resolve(new ZamaRec.Core.Config.ProfileConfig { Name = "CS2" })!.Id);
        Xunit.Assert.Equal("black-ops-3", ZamaRec.Core.Config.GamePresets.Resolve(new ZamaRec.Core.Config.ProfileConfig { Name = "Black Ops III" })!.Id);
        Xunit.Assert.Null(ZamaRec.Core.Config.GamePresets.Resolve(new ZamaRec.Core.Config.ProfileConfig { Name = "factorio" }));
    }

    [Xunit.Fact]
    public void Game_key_round_trips_through_toml()
    {
        var cfg = ZamaRec.Core.Config.ConfigLoader.Parse(ZamaRec.Core.Config.DefaultConfig.Toml);
        cfg.Profiles[0].Game = "cs2"; cfg.Profiles[0].SteamAppId = 730; cfg.Profiles[1].BannerUrl = "https://example.org/mc.jpg";
        var back = ZamaRec.Core.Config.ConfigLoader.Parse(ZamaRec.Core.Config.ConfigLoader.Serialize(cfg));
        Xunit.Assert.Equal("cs2", back.Profiles[0].Game);
        Xunit.Assert.Equal(730, back.Profiles[0].SteamAppId);
        Xunit.Assert.Equal("https://example.org/mc.jpg", back.Profiles[1].BannerUrl);
    }
}
