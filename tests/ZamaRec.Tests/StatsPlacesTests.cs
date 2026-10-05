using System.Text.Json;
using ZamaRec.Core;
using ZamaRec.Core.Telemetry;

namespace ZamaRec.Tests;

public class StatsPlacesTests
{
    private static string Dir(string name) => Path.Combine(Path.GetTempPath(), "zamarec-places", name);

    [Fact]
    public void EachFolderKeepsItsOwnFigures()
    {
        var c = new StatsCounters();
        c.Place(Dir("a")).FootageBytes += 100;
        c.Place(Dir("b")).FootageBytes += 7;
        c.Place(Dir("a") + Path.DirectorySeparatorChar).FootageBytes += 50;   // the same folder, written with a trailing separator
        Assert.Equal(2, c.Places.Count);
        Assert.Equal(150, c.Place(Dir("a")).FootageBytes);
        Assert.Equal(7, c.Place(Dir("b")).FootageBytes);
    }

    [Fact]
    public void FolderNamesCompareTheWayTheFileSystemDoes()
    {
        var c = new StatsCounters();
        c.Place(Dir("Videos")).ClipBytes = 1;
        c.Place(Dir("videos")).ClipBytes += 1;
        Assert.Equal(Platform.IsWindows ? 1 : 2, c.Places.Count);
    }

    [Fact]
    public void EarlierTotalsGoToTheRecordingsFolderOnce()
    {
        var c = new StatsCounters { DiskBytes = 500, ClipBytes = 40, Clips = 3, Sessions = 9 };
        c.SeedPlaces(Dir("root"));
        var p = c.Place(Dir("root"));
        Assert.Equal((500, 40, 3, 9, true), (p.FootageBytes, p.ClipBytes, p.Clips, p.Sessions, p.Seeded));

        c.DiskBytes = 900;
        c.SeedPlaces(Dir("other"));   // already counted per folder: nothing is seeded again
        Assert.Single(c.Places);
        Assert.Equal(500, p.FootageBytes);
    }

    [Fact]
    public void TheSessionCountIsNotGuessedWhenSomeWentToRam()
    {
        var c = new StatsCounters { DiskBytes = 500, RamBytes = 1, Sessions = 9 };
        c.SeedPlaces(Dir("root"));
        Assert.Equal(0, c.Place(Dir("root")).Sessions);
    }

    [Fact]
    public void PlacesSurviveTheStatsFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "zamarec-stats-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new StatsStore(file).Update(c => { var p = c.Place(Dir("a")); p.FootageBytes = 12; p.Sessions = 1; });
            var again = new StatsStore(file);
            Assert.Equal(12, again.Counters.Place(Dir("a")).FootageBytes);
            Assert.Single(again.PlacesSnapshot());
            Assert.Contains("\"places\"", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }
}
