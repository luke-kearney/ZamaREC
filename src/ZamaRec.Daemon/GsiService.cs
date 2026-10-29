using System.Text.Json;
using ZamaRec.Core;
using ZamaRec.Core.Config;
using ZamaRec.Core.Gsi;
using ZamaRec.Core.Telemetry;

namespace ZamaRec.Daemon;

/// <summary>Receives CS2 game state payloads, runs the detector, and turns events into markers on the active session.</summary>
public sealed class GsiService
{
    private readonly ZamaRecConfig _cfg;
    private readonly RecordingCoordinator _rec;
    private readonly EventLog _events;
    private readonly ILogger<GsiService> _log;
    private readonly object _lock = new();
    private GsiEventDetector _detector = new();
    private string? _detectorSession;
    private long _lastMono;
    private string? _lastMap;
    private Protocol.GameState? _game;

    public string Token { get; }
    public string? CfgPath { get; private set; }

    public GsiService(ZamaRecConfig cfg, RecordingCoordinator rec, EventLog events, ILogger<GsiService> log)
    {
        _cfg = cfg; _rec = rec; _events = events; _log = log;
        Token = GsiConfigFile.LoadOrCreateToken();
    }

    /// <summary>Writes the cfg into CS2's folder. Called at startup and after a config reload.</summary>
    public void EnsureConfigFile()
    {
        try
        {
            CfgPath = GsiConfigFile.EnsureWritten(_cfg.Daemon.GsiPort, Token, _cfg.Gsi.CfgDir, out var changed);
            if (CfgPath is null) { _log.LogWarning("CS2 cfg folder not found; set gsi.cfg_dir to enable game events"); return; }
            if (changed) { _log.LogInformation("wrote {path} (CS2 reads it at launch)", CfgPath); _events.Write("config", "wrote CS2 game state cfg; restart CS2 if it is running", file: GsiConfigFile.FileName); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "could not write the CS2 cfg"); }
    }

    public double? SecondsSinceLastPayload => _lastMono == 0 ? null : (MonotonicClock.NowMicros() - _lastMono) / 1e6;
    public string? CurrentMap => _lastMap;

    /// <summary>The match as the last payloads described it (map, score, the player's own stats); null in the menu.</summary>
    public Protocol.GameState? Game { get { lock (_lock) return _game; } }

    // CS2 sends only the blocks that exist: no map block in the menu, and the player block can be someone being
    // spectated. Stats are taken from the player's own block only, and start over with each map.
    private void UpdateGame(GsiSnapshot s)
    {
        if (s.MapName is null) { _game = null; return; }
        var prev = _game?.Map == s.MapName ? _game : null;
        var own = s.IsOwnPlayer;
        _game = new Protocol.GameState
        {
            Map = s.MapName, Mode = s.MapMode ?? prev?.Mode, Phase = s.MapPhase ?? prev?.Phase, Round = s.MapRound ?? prev?.Round,
            ScoreCt = s.ScoreCt ?? prev?.ScoreCt, ScoreT = s.ScoreT ?? prev?.ScoreT,
            Team = (own ? s.PlayerTeam : null) ?? prev?.Team,
            Kills = (own ? s.MatchKills : null) ?? prev?.Kills, Assists = (own ? s.MatchAssists : null) ?? prev?.Assists,
            Deaths = (own ? s.MatchDeaths : null) ?? prev?.Deaths, Mvps = (own ? s.MatchMvps : null) ?? prev?.Mvps,
        };
    }

    /// <summary>Handles one POST from the game. Returns false when the token does not match.</summary>
    public async Task<bool> HandleAsync(JsonElement root, long arrivalMono, CancellationToken ct)
    {
        if (!root.TryGetProperty("auth", out var auth) || !auth.TryGetProperty("token", out var tok) || tok.GetString() != Token)
            return false;
        _lastMono = arrivalMono;
        var snap = GsiSnapshot.Parse(root);
        List<GsiEvent> events;
        var sessionId = _rec.ActiveSessionId;
        lock (_lock)
        {
            if (sessionId != _detectorSession) { _detector = new GsiEventDetector(); _detectorSession = sessionId; }
            events = _detector.Process(snap);
            _lastMap = _detector.CurrentMap;
            UpdateGame(snap);
        }
        if (_cfg.Gsi.CaptureRaw && _rec.ActiveSessionDir is string dir)
        {
            try { await File.AppendAllTextAsync(Path.Combine(dir, "gsi.jsonl"), JsonSerializer.Serialize(new { mono_us = arrivalMono, payload = root }) + "\n", ct); }
            catch (Exception ex) { _log.LogDebug(ex, "gsi capture"); }
        }
        var mode = Game?.Mode;
        foreach (var ev in events)
        {
            if (ev.Type == "map_start" && snap.MapName is not null) _rec.SetMap(snap.MapName, mode);
            await _rec.EventMarkAsync(ev, arrivalMono, mode, ct);
        }
        return true;
    }
}
