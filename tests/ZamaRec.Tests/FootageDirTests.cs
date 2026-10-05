using ZamaRec.Core.Config;
using ZamaRec.Core.Sessions;

namespace ZamaRec.Tests;

public class FootageDirTests
{
    private static (ZamaRecConfig Cfg, ProfileConfig Profile, string Tmp) Setup(string? footage)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "zamarec-test-" + Guid.NewGuid().ToString("N"));
        var cfg = new ZamaRecConfig();
        cfg.Storage.Root = Path.Combine(tmp, "root");
        cfg.Storage.FootageDir = footage is null ? null : footage.Replace("{tmp}", tmp);
        cfg.Timeline.Storage = "disk";
        var profile = new ProfileConfig { Name = "game", Mode = "timeline" };
        return (cfg, profile, tmp);
    }

    [Fact]
    public void WithoutAFootageFolderTheSegmentsGoIntoTheSessionFolder()
    {
        var (cfg, profile, tmp) = Setup(null);
        try
        {
            var s = SessionStore.Create(cfg, profile, "test", null);
            Assert.Null(s.Info.MediaDir);
            Assert.Equal(s.Dir, s.MediaDir);
            Assert.False(s.Info.InRam);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void AFootageFolderTakesTheSegmentsAndIsNotRam()
    {
        var (cfg, profile, tmp) = Setup(Path.Combine("{tmp}", "footage"));
        try
        {
            var s = SessionStore.Create(cfg, profile, "test", null);
            Assert.Equal(Path.Combine(tmp, "footage", s.Info.Id), s.MediaDir);
            Assert.True(Directory.Exists(s.MediaDir));
            Assert.StartsWith(Path.Combine(tmp, "root", "sessions"), s.Dir);   // details stay under root
            Assert.True(s.Info.MediaPlain);
            Assert.False(s.Info.InRam);

            var again = SessionStore.Load(s.Dir);   // and it survives the round trip through session.json
            Assert.True(again.Info.MediaPlain);
            Assert.Equal(s.MediaDir, again.MediaDir);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void AFootageFolderThatCannotBeMadeFallsBackToTheSessionFolder()
    {
        var (cfg, profile, tmp) = Setup(null);
        try
        {
            // a file where the folder should be: creating the session's folder inside it fails on every system
            Directory.CreateDirectory(tmp);
            var blocker = Path.Combine(tmp, "not-a-folder");
            File.WriteAllText(blocker, "");
            cfg.Storage.FootageDir = blocker;
            var s = SessionStore.Create(cfg, profile, "test", null);
            Assert.Null(s.Info.MediaDir);
            Assert.False(s.Info.MediaPlain);
            Assert.Equal(s.Dir, s.MediaDir);
        }
        finally { Directory.Delete(tmp, true); }
    }
}
