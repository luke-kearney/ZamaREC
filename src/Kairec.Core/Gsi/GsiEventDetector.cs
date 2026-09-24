using System.Text.Json;

namespace Kairec.Core.Gsi;

/// <summary>A game event worth a marker. Type names double as marker types.</summary>
public sealed record GsiEvent(string Type, string? Label, Dictionary<string, JsonElement> Data)
{
    public static GsiEvent Of(string type, string? label = null, params (string Key, object Value)[] data)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in data) d[k] = JsonSerializer.SerializeToElement(v);
        return new GsiEvent(type, label, d);
    }
}

/// <summary>
/// Turns consecutive game state snapshots into events. Pure and deterministic: feed it payloads in
/// order and it returns the events each one implies. Player-derived events only ever come from
/// payloads whose player block is the provider's own steamid, so spectating never counts.
/// </summary>
public sealed class GsiEventDetector
{
    private GsiSnapshot? _lastOwn;      // last payload that described our own player
    private string? _map;
    private int? _round;
    private string? _roundPhase;
    private bool _roundScored;
    private bool _mapEnded;

    public string? CurrentMap => _map;

    public List<GsiEvent> Process(GsiSnapshot s)
    {
        var events = new List<GsiEvent>();

        // --- map lifecycle (independent of who the player block describes)
        if (s.MapName is not null && s.MapName != _map)
        {
            if (_map is not null && !_mapEnded) events.Add(GsiEvent.Of("map_end", _map, ("map", _map), ("reason", "map change")));
            _map = s.MapName; _lastOwn = null; _round = null; _roundPhase = null; _roundScored = false; _mapEnded = false;
            events.Add(GsiEvent.Of("map_start", s.MapName, ("map", s.MapName), ("mode", s.MapPhase ?? "")));
        }
        else if (s.MapName is null && _map is not null && s.ProviderSteamId is not null && s.PlayerActivity is "menu")
        {
            if (!_mapEnded) events.Add(GsiEvent.Of("map_end", _map, ("map", _map), ("reason", "left")));
            _map = null; _lastOwn = null; _round = null; _roundPhase = null; _mapEnded = false;
        }
        if (s.MapPhase == "gameover" && _map is not null && !_mapEnded)
        {
            events.Add(GsiEvent.Of("map_end", _map, ("map", _map), ("reason", "gameover")));
            _mapEnded = true;
        }

        // --- round result
        if (s.MapRound is int rn && rn != _round) { _round = rn; _roundScored = false; }
        if (s.RoundPhase == "over" && !_roundScored && s.RoundWinTeam is not null)
        {
            var myTeam = s.IsOwnPlayer ? s.PlayerTeam : _lastOwn?.PlayerTeam;
            if (myTeam is not null)
            {
                var won = string.Equals(myTeam, s.RoundWinTeam, StringComparison.OrdinalIgnoreCase);
                events.Add(GsiEvent.Of(won ? "round_win" : "round_loss", $"round {(_round ?? 0) + 1}", ("round", (_round ?? 0) + 1), ("win_team", s.RoundWinTeam)));
                _roundScored = true;
            }
        }
        if (s.RoundPhase is not null && s.MapPhase != "gameover") _roundPhase = s.RoundPhase;

        // --- everything below needs the player block to be us
        if (!s.IsOwnPlayer) return events;
        var prev = _lastOwn;
        if (prev is not null)
        {
            // Round kills reset when a new round starts, so only an increase counts.
            if (s.RoundKills is int rk && prev.RoundKills is int pk && rk > pk)
            {
                var hs = s.RoundKillsHs is int h && prev.RoundKillsHs is int ph && h > ph;
                var count = rk;
                var label = count >= 5 ? "ace" : count >= 4 ? "4k" : count >= 3 ? "3k" : count == 2 ? "2k" : (hs ? "headshot" : null);
                events.Add(GsiEvent.Of("kill", label, ("count", count), ("headshot", hs)));
            }
            var died = (s.Health is int hp && hp <= 0 && prev.Health is int php && php > 0)
                    || (s.MatchDeaths is int d && prev.MatchDeaths is int pd && d > pd);
            if (died) events.Add(GsiEvent.Of("death", null, ("round", (_round ?? 0) + 1)));
            if (s.MatchMvps is int mv && prev.MatchMvps is int pmv && mv > pmv)
                events.Add(GsiEvent.Of("mvp", $"mvp #{mv}", ("mvps", mv)));
        }
        _lastOwn = s;
        return events;
    }
}
