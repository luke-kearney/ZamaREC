using Kairec.Core.Config;
using Kairec.Core.Export;
using Kairec.Core.Recording;

namespace Kairec.Tests;

public class AudioTrackTests
{
    private const string Tracks = """
        [capture]
        audio = ["default_output", "default_input"]
        multitrack = true
        [[capture.tracks]]
        name = "game"
        sources = ["app-inverse:discord"]
        [[capture.tracks]]
        name = "mic"
        sources = ["default_input"]
        [[capture.tracks]]
        name = "discord"
        sources = ["app:discord"]
        [[profiles]]
        name = "x"
        match = "x"
        """;

    private static List<string> AudioArgs(KairecConfig cfg)
    {
        var args = GsrArguments.Build(cfg, cfg.Effective(cfg.Profiles[0]), "/m", "/b", "/s");
        return Enumerable.Range(0, args.Count - 1).Where(i => args[i] == "-a").Select(i => args[i + 1]).ToList();
    }

    [Fact]
    public void OneTrackByDefault()
    {
        var cfg = ConfigLoader.Parse("[capture]\naudio = [\"default_output\", \"default_input\"]\n[[profiles]]\nname = \"x\"\nmatch = \"x\"\n");
        Assert.False(cfg.Capture.Multitrack);
        Assert.Equal(["default_output|default_input"], AudioArgs(cfg));
        Assert.Equal(["mix"], cfg.Capture.EffectiveTracks().Select(t => t.Name));
    }

    [Fact]
    public void MultitrackPutsTheMixFirstThenEachTrack()
    {
        var cfg = ConfigLoader.Parse(Tracks);
        Assert.Equal(["default_output|default_input", "app-inverse:discord", "default_input", "app:discord"], AudioArgs(cfg));
        Assert.Equal(["mix", "game", "mic", "discord"], cfg.Capture.EffectiveTracks().Select(t => t.Name));

        // configured but switched off: still one track
        cfg.Capture.Multitrack = false;
        Assert.Equal(["default_output|default_input"], AudioArgs(cfg));
    }

    [Fact]
    public void TracksRoundTripAndAreValidated()
    {
        var back = ConfigLoader.Parse(ConfigLoader.Serialize(ConfigLoader.Parse(Tracks)));
        Assert.True(back.Capture.Multitrack);
        Assert.Equal(["game", "mic", "discord"], back.Capture.Tracks.Select(t => t.Name));
        Assert.Equal(["app-inverse:discord"], back.Capture.Tracks[0].Sources);

        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[[capture.tracks]]\nname = \"mix\"\nsources = [\"default_input\"]\n"));
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[[capture.tracks]]\nname = \"a\"\nsources = [\"x\"]\n[[capture.tracks]]\nname = \"A\"\nsources = [\"y\"]\n"));
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[[capture.tracks]]\nname = \"a\"\nsources = []\n"));
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[[capture.tracks]]\nsources = [\"x\"]\n"));
    }

    [Fact]
    public void ClipsKeepAndTitleEveryTrack()
    {
        Assert.Equal(["-map", "0:v:0", "-map", "0:a?"], ClipExporter.AllTracks(null));
        Assert.Equal(["-map", "0:v:0", "-map", "0:a?", "-metadata:s:a:0", "title=mix", "-metadata:s:a:0", "handler_name=mix", "-metadata:s:a:1", "title=game", "-metadata:s:a:1", "handler_name=game"],
            ClipExporter.AllTracks(["mix", "game"]));
    }
}
