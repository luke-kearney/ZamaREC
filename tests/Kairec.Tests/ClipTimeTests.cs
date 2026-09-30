using System.Text.Json;
using Kairec.Core.Config;
using Kairec.Core.Sessions;

namespace Kairec.Tests;

public class ClipTimeTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 21, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void ASessionMapsTimelineSecondsToTheWallClock()
    {
        // timeline: the recorder's first-frame stamp
        var timeline = new SessionInfo { StartedAt = Monday, SpawnMonoUs = 1_000_000, FirstFrameMonoUs = 1_650_000, FirstFrameRealtimeUs = Monday.AddMilliseconds(650).ToUnixTimeMilliseconds() * 1000 };
        Assert.Equal(Monday.AddMilliseconds(60_650), timeline.TimeAt(60));
        Assert.Equal(TimeSpan.FromHours(2), timeline.TimeAt(60).Offset);
        // buffer: the origin is the recorder spawn
        var buffer = new SessionInfo { StartedAt = Monday, SpawnMonoUs = 5_000_000, FirstFrameMonoUs = 5_000_000 };
        Assert.Equal(Monday.AddSeconds(90), buffer.TimeAt(90));
        Assert.Equal(Monday.AddSeconds(5), new SessionInfo { StartedAt = Monday }.TimeAt(5));
    }

    [Fact]
    public void AClipIsDatedByItsFootage()
    {
        var root = Directory.CreateTempSubdirectory("kairec-test").FullName;
        try
        {
            var cfg = ConfigLoader.Parse($"[storage]\nroot = \"{root}\"\n");
            var cat = new SessionCatalog(cfg);
            var dir = Path.Combine(cfg.SessionsDir, "s1");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(new SessionInfo { Id = "s1", StartedAt = Monday, SpawnMonoUs = 0, FirstFrameMonoUs = 0 }, SessionJson.Default.SessionInfo));
            var today = Monday.AddDays(2);

            // mark and save clips are written as they happen
            Assert.Equal(Monday, cat.FootageTime(new ClipInfo { Source = "mark", CreatedAt = Monday, SessionId = "s1", SessionOffset = 0, DurationSeconds = 30 }));
            // an export says when its footage was recorded
            Assert.Equal(Monday, cat.FootageTime(new ClipInfo { Source = "export", CreatedAt = today, RecordedAt = Monday }));
            // one from before recorded_at existed is placed through its session: offset + length
            Assert.Equal(Monday.AddSeconds(130), cat.FootageTime(new ClipInfo { Source = "export", CreatedAt = today, SessionId = "s1", SessionOffset = 100, DurationSeconds = 30 }));
            // and falls back to when it was cut if the session is gone
            Assert.Equal(today, cat.FootageTime(new ClipInfo { Source = "export", CreatedAt = today, SessionId = "gone", SessionOffset = 100, DurationSeconds = 30 }));

            // the list sorts by footage time, and the file name can carry it
            var path = ClipNaming.NewPath(cfg.ClipsDir, "game", "cut", at: Monday);
            Assert.Equal("2026-09-28_21-00-00_cut.mp4", Path.GetFileName(path));
            File.WriteAllText(path, "x"); ClipNaming.WriteSidecar(path, new ClipInfo { Profile = "game", Source = "export", CreatedAt = today, RecordedAt = Monday });
            var newer = ClipNaming.NewPath(cfg.ClipsDir, "game", "later", at: Monday.AddDays(1));
            File.WriteAllText(newer, "x"); ClipNaming.WriteSidecar(newer, new ClipInfo { Profile = "game", Source = "save", CreatedAt = Monday.AddDays(1) });
            var clips = cat.Clips();
            Assert.Equal(["2026-09-29_21-00-00_later.mp4", "2026-09-28_21-00-00_cut.mp4"], clips.Select(c => c.File));
            Assert.Equal(Monday, clips[1].At);
            Assert.Equal(today, clips[1].Info.CreatedAt);   // retention still counts from when it was written
        }
        finally { Directory.Delete(root, true); }
    }
}
