using ZamaRec.Core.Config;
using ZamaRec.Core.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZamaRec.Tests;

public class SteamLaunchTests
{
    private const string Reaper = "/home/u/.local/share/Steam/ubuntu12_32/reaper\0SteamLaunch\0AppId=892970\0--\0/lib/SteamLinuxRuntime_soldier/_v2-entry-point\0--verb=waitforexitandrun\0--\0/games/Valheim/valheim.x86_64\0";

    [Fact]
    public void ParsesTheAppIdOfALaunchWrapper()
    {
        Assert.Equal(892970, SteamLibrary.ParseLaunch(Reaper));
        Assert.Equal(730, SteamLibrary.ParseLaunch("bash ./reaper SteamLaunch AppId=730 -- cs2.sh"));
        Assert.Null(SteamLibrary.ParseLaunch("/usr/bin/reaper\0--daemon\0"));
        Assert.Null(SteamLibrary.ParseLaunch("reaper\0SteamLaunch\0AppId=abc\0"));
        Assert.Null(SteamLibrary.ParseLaunch("game\0--\0something\0SteamLaunch\0AppId=1\0"));   // only near the start
    }

    [Fact]
    public void ReadsTheNameFromTheManifest()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, "steamapps"));
        File.WriteAllText(Path.Combine(t.Path, "steamapps", "appmanifest_252950.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"252950\"\n\t\"name\"\t\t\"Rocket League®\"\n\t\"installdir\"\t\t\"rocketleague\"\n}\n");
        Assert.Equal("Rocket League", SteamLibrary.AppName(252950, [t.Path]));
        Assert.Null(SteamLibrary.AppName(1, [t.Path]));
        Assert.Equal("Fate-stay night", SteamLibrary.SafeName("Fate/stay night™"));
    }

    [Fact]
    public void ScanReportsLaunchesAndLetsAProfileMatchWin()
    {
        using var t = new TempDir();
        t.Proc(100, "reaper", Reaper);
        t.Proc(101, "valheim.x86_64", "/games/Valheim/valheim.x86_64\0");
        t.Proc(102, "bash", "bash\0");

        var none = new ProcessWatcher([], NullLogger.Instance, t.Path).Scan(0);
        Assert.Null(none.Match);
        Assert.Equal([new SteamLaunch(100, 892970)], none.Launches);

        var profile = new ProfileConfig { Name = "valheim", Match = "^valheim" };
        var hit = new ProcessWatcher([profile], NullLogger.Instance, t.Path).Scan(0);
        Assert.Equal(101, hit.Match!.Pid);
        Assert.Equal([new SteamLaunch(100, 892970)], new ProcessWatcher([profile], NullLogger.Instance, t.Path).SteamLaunches());
    }

    private static ZamaRecConfig Config(params ProfileConfig[] profiles)
    {
        var cfg = ConfigLoader.Parse("[capture]\nquality = \"medium\"\n");
        cfg.Profiles.AddRange(profiles);
        return cfg;
    }

    private static readonly SteamLaunch Valheim = new(100, 892970);
    private static string? Name(long id) => id == 892970 ? "Valheim" : id == 252950 ? "Rocket League" : id == 4628710 ? "Proton 11.0" : null;

    [Fact]
    public void AnUnconfirmedGameWaitsOutTheGracePeriod()
    {
        var tr = new SteamLaunchTracker(); var cfg = Config();
        Assert.Null(tr.Decide([Valheim], cfg, 0, Name));
        Assert.Equal([Valheim], tr.New);
        Assert.Null(tr.Decide([Valheim], cfg, 29_000, Name));
        Assert.Empty(tr.New);
        var d = tr.Decide([Valheim], cfg, 30_000, Name);
        Assert.NotNull(d);
        Assert.Null(d!.Profile);
        Assert.Equal("Valheim", d.Name);
    }

    [Fact]
    public void IgnoredDisabledUnnamedAndToolLaunchesAreNeverRecorded()
    {
        var cfg = Config(); cfg.AutoGames.Ignore.Add(892970);
        Assert.Null(new SteamLaunchTracker { Grace = TimeSpan.Zero }.Decide([Valheim], cfg, 0, Name));

        cfg = Config(); cfg.AutoGames.Enabled = false;
        Assert.Null(new SteamLaunchTracker { Grace = TimeSpan.Zero }.Decide([Valheim], cfg, 0, Name));

        cfg = Config();
        Assert.Null(new SteamLaunchTracker { Grace = TimeSpan.Zero }.Decide([new SteamLaunch(5, 3_000_000_000), new SteamLaunch(6, 12345), new SteamLaunch(7, 431960), new SteamLaunch(8, 4628710)], cfg, 0, Name));
    }

    [Fact]
    public void AProfileClaimsItsAppId()
    {
        // No process match: Steam launching the app starts the profile at once.
        var byApp = new ProfileConfig { Name = "Valheim", SteamAppId = 892970 };
        var d = new SteamLaunchTracker().Decide([Valheim], Config(byApp), 0, Name);
        Assert.Same(byApp, d!.Profile);

        // With a process match the launch is left alone, however long it takes.
        var byRegex = new ProfileConfig { Name = "vh", SteamAppId = 892970, Match = "^valheim" };
        Assert.Null(new SteamLaunchTracker { Grace = TimeSpan.Zero }.Decide([Valheim], Config(byRegex), 99_000, Name));

        // A profile named like the game but without the app id: same.
        var byName = new ProfileConfig { Name = "rocket league", MatchCmdline = "RocketLeague\\.exe" };
        Assert.Null(new SteamLaunchTracker { Grace = TimeSpan.Zero }.Decide([new SteamLaunch(9, 252950)], Config(byName), 0, Name));

        // An app a process-matched profile recorded before is left alone too.
        Assert.Null(new SteamLaunchTracker { Grace = TimeSpan.Zero }.Decide([Valheim], Config(), 0, Name, id => id == 892970));

        // The cs2 preset carries app id 730.
        Assert.Equal("cs2", Config(new ProfileConfig { Name = "cs2", Match = "^cs2$" }).ProfileForSteamApp(730)!.Name);
    }

    [Fact]
    public void StaleLaunchesStayUnrecordedUntilTheyExit()
    {
        var tr = new SteamLaunchTracker { Grace = TimeSpan.Zero }; var cfg = Config();
        tr.MarkStale([Valheim]);
        Assert.Null(tr.Decide([Valheim], cfg, 0, Name));
        Assert.Null(tr.Decide([], cfg, 1000, Name));                  // it exited
        Assert.NotNull(tr.Decide([Valheim], cfg, 2000, Name));        // a fresh launch reusing the pid
        tr.MarkStale(100);
        Assert.Null(tr.Decide([Valheim], cfg, 3000, Name));
        tr.ClearStale();
        Assert.NotNull(tr.Decide([Valheim], cfg, 4000, Name));
    }

    [Fact]
    public void TheAutoProfileIsARamTimelineSizedToTheBudget()
    {
        var cfg = Config();   // medium = 12000 kbps in the built-in presets
        var e = cfg.Effective(cfg.AutoProfile(892970, "Valheim"));
        Assert.Equal(CaptureMode.Timeline, e.Mode);
        Assert.Equal("ram", e.TimelineStorage);
        Assert.Equal(20, e.WindowMinutes);                            // 2 GB / 1.1 / 1.5 MB/s = 20.2 min
        Assert.True(e.WindowBytes() * 11 / 10 <= 2_000_000_000);
        Assert.True(e.MarkSaves);
        Assert.Equal(1, new AutoGamesConfig { RamGb = 0.01 }.WindowMinutes(35000));
    }

    [Fact]
    public void AutoGamesRoundTripsAndAProfileMayMatchByAppIdAlone()
    {
        var cfg = ConfigLoader.Parse("[auto_games]\nenabled = false\nram_gb = 3.5\nquality = \"low\"\nignore = [431960, 12]\n[[profiles]]\nname = \"Valheim\"\nsteam_app_id = 892970\n");
        var back = ConfigLoader.Parse(ConfigLoader.Serialize(cfg));
        Assert.False(back.AutoGames.Enabled);
        Assert.Equal(3.5, back.AutoGames.RamGb);
        Assert.Equal("low", back.AutoGames.Quality);
        Assert.Equal([431960L, 12L], back.AutoGames.Ignore);
        Assert.Equal(892970, back.Profiles[0].SteamAppId);
        Assert.True(ConfigLoader.Parse("").AutoGames.Enabled);
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[[profiles]]\nname = \"x\"\n"));
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[auto_games]\nquality = \"nope\"\n"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("zamarec-test").FullName;
        public void Proc(int pid, string comm, string cmdline)
        {
            var d = System.IO.Path.Combine(Path, pid.ToString());
            Directory.CreateDirectory(d);
            File.WriteAllText(System.IO.Path.Combine(d, "comm"), comm + "\n");
            File.WriteAllText(System.IO.Path.Combine(d, "cmdline"), cmdline);
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
