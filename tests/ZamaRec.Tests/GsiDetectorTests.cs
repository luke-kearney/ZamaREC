using System.Text.Json;
using ZamaRec.Core.Gsi;

namespace ZamaRec.Tests;

public class GsiDetectorTests
{
    private static List<(string Note, GsiSnapshot Snap)> LoadMatch()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gsi_match.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("note").GetString()!, GsiSnapshot.Parse(e.GetProperty("payload")))).ToList();
    }

    private static List<(string Note, GsiEvent Ev)> Run()
    {
        var det = new GsiEventDetector();
        var all = new List<(string, GsiEvent)>();
        foreach (var (note, snap) in LoadMatch())
            foreach (var ev in det.Process(snap)) all.Add((note, ev));
        return all;
    }

    [Fact]
    public void ParsesTheFieldsWeNeed()
    {
        var s = LoadMatch()[4].Snap;   // kill 2 hs
        Assert.Equal("76561198000000001", s.ProviderSteamId);
        Assert.True(s.IsOwnPlayer);
        Assert.Equal("de_mirage", s.MapName);
        Assert.Equal(2, s.RoundKills);
        Assert.Equal(1, s.RoundKillsHs);
        Assert.Equal("CT", s.PlayerTeam);
    }

    [Fact]
    public void KillsCarryTheRoundCountAndHeadshotFlag()
    {
        var kills = Run().Where(x => x.Ev.Type == "kill").ToList();
        Assert.Equal(4, kills.Count);   // 3 in round 1, 1 in round 3; none of the spectated ones
        Assert.Equal([1, 2, 3, 1], kills.Select(k => k.Ev.Data["count"].GetInt32()).ToArray());
        Assert.Equal([false, true, false, false], kills.Select(k => k.Ev.Data["headshot"].GetBoolean()).ToArray());
        Assert.Equal("3k", kills[2].Ev.Label);
        Assert.Equal("2k", kills[1].Ev.Label);   // count label wins; the headshot is in the data
    }

    [Fact]
    public void SpectatedPlayerNeverCounts()
    {
        var evs = Run();
        Assert.DoesNotContain(evs, x => x.Note.StartsWith("mate") || x.Note.StartsWith("spectating"));
        // my own death is one event, the observed player's death is none
        Assert.Single(evs, x => x.Ev.Type == "death");
        Assert.Equal("i die", evs.Single(x => x.Ev.Type == "death").Note);
    }

    [Fact]
    public void RoundResultsUseMyTeamEvenWhileSpectating()
    {
        var evs = Run();
        var rounds = evs.Where(x => x.Ev.Type is "round_win" or "round_loss").ToList();
        Assert.Equal(["round_win", "round_loss", "round_win"], rounds.Select(r => r.Ev.Type).ToArray());
        Assert.Equal("round over loss", rounds[1].Note);   // scored from the T win while I was dead and watching a CT
    }

    [Fact]
    public void MvpAndMapLifecycle()
    {
        var evs = Run();
        Assert.Single(evs, x => x.Ev.Type == "mvp");
        var maps = evs.Where(x => x.Ev.Type is "map_start" or "map_end").Select(x => (x.Note, x.Ev.Type)).ToList();
        Assert.Equal(("map live", "map_start"), maps[0]);
        Assert.Contains(("gameover", "map_end"), maps);
        Assert.Equal(2, maps.Count);   // one start, one end; leaving to the menu after gameover adds nothing
        Assert.Equal("de_mirage", evs.First(x => x.Ev.Type == "map_start").Ev.Data["map"].GetString());
    }

    [Fact]
    public void NoEventsWithoutPlayerBlockOrOnRepeat()
    {
        var det = new GsiEventDetector();
        var s = LoadMatch()[3].Snap;   // kill 1
        det.Process(LoadMatch()[2].Snap);
        Assert.Single(det.Process(s));
        Assert.Empty(det.Process(s));   // identical payload again: nothing new
        Assert.Empty(det.Process(GsiSnapshot.Parse("{\"provider\":{\"steamid\":\"76561198000000001\"}}")));
    }
}
