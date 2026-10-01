using ZamaRec.Core.Config;
using ZamaRec.Core.Processes;

namespace ZamaRec.Daemon;

/// <summary>
/// Starts a session when a profile's process appears, or when Steam launches a game (one a profile claims by
/// app id, or an unconfirmed one recorded with the [auto_games] defaults). Stops it when that process exits.
/// </summary>
public sealed class GameWatcherService : BackgroundService
{
    private readonly ZamaRecConfig _cfg;
    private readonly RecordingCoordinator _rec;
    private readonly ILogger<GameWatcherService> _log;
    private readonly PauseControl _pause;
    private readonly SeenGames _seen;
    private readonly SteamLaunchTracker _steam = new();
    private readonly Dictionary<long, (string? Name, long At)> _names = new();
    private ProcessWatcher _watcher;
    private int _watcherVersion;
    private int _failedPid; private long _retryAt;
    private bool _wasActive, _wasPaused;
    private string? _lastSessionId;
    /// <summary>The session this watcher started last: its game pid, Steam app, and whether that pid is Steam's launch wrapper.</summary>
    private (string? SessionId, int Pid, long? AppId, bool ByLaunch)? _started;

    public GameWatcherService(ZamaRecConfig cfg, RecordingCoordinator rec, PauseControl pause, SeenGames seen, ILogger<GameWatcherService> log)
    {
        _cfg = cfg; _rec = rec; _pause = pause; _seen = seen; _log = log;
        _watcher = new ProcessWatcher(cfg.Profiles, log);
        _watcherVersion = cfg.Version;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var self = Environment.ProcessId;
        var interval = TimeSpan.FromSeconds(Math.Max(0.25, _cfg.Daemon.ProcessScanSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _rec.TickAsync(ct);
                if (_cfg.Version != _watcherVersion) { _watcher = new ProcessWatcher(_cfg.Profiles, _log); _watcherVersion = _cfg.Version; }
                NoteSessionEnd();   // one stopped from outside since the last pass

                if (_rec.IsActive)
                {
                    if (_rec.RecorderDied)
                    {
                        _log.LogWarning("recorder exited unexpectedly; closing session");
                        await _rec.StopAsync("recorder died", ct);
                    }
                    else if (_rec.ActiveGamePid is int pid && !ProcessWatcher.IsAlive(pid))
                    {
                        _log.LogInformation("game process {pid} exited", pid);
                        await _rec.StopAsync("game exited", ct);
                    }
                    else if (_rec.ActiveUnconfirmed && _watcher.FindRunning(self) is { } game)
                    {
                        // A profile's process can show up long after Steam's wrapper (launchers, shader compiles): the profile takes over.
                        _log.LogInformation("{profile} (pid {pid}) appeared during an unconfirmed session; switching to its profile", game.Profile.Name, game.Pid);
                        await _rec.StopAsync("a profile matched", ct);
                        await StartAsync(game.Profile, game.Pid, SteamLibrary.AppIdOfProcess(game.Pid), unconfirmed: false, ct);
                    }
                }
                else
                {
                    var paused = _pause.IsPaused(ProcessWatcher.IsAlive);
                    if (_wasPaused && !paused) _steam.ClearStale();   // turned back on: pick up whatever is running
                    _wasPaused = paused;
                    if (!paused) await ScanAsync(self, ct);
                }
                NoteSessionEnd();   // one this pass stopped
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "watcher loop error"); }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// When a session ends, the Steam launch it belonged to must not start another one: a launcher that lingers
    /// after the game, or a game stopped by hand. Other launches are left alone. Steam may run a game as two
    /// launches in a row (The Witcher 3 does), and the second must still be picked up when the first exits.
    /// </summary>
    private void NoteSessionEnd()
    {
        var id = _rec.ActiveSessionId;
        if (_wasActive && id != _lastSessionId)
        {
            var launches = _watcher.SteamLaunches();
            if (_started is not { } mine || mine.SessionId != _lastSessionId) _steam.MarkStale(launches);          // started by hand: leave everything alone
            else if (mine.ByLaunch && !ProcessWatcher.IsAlive(mine.Pid)) { }                                       // that launch is over; whatever else runs is a new one
            else if (mine.AppId is null) _steam.MarkStale(launches);                                               // cannot tell which launch was its own
            else _steam.MarkStale(launches.Where(l => l.Pid == mine.Pid || l.AppId == mine.AppId));
        }
        _wasActive = id is not null; _lastSessionId = id;
    }

    private async Task ScanAsync(int self, CancellationToken ct)
    {
        var scan = _watcher.Scan(self);
        if (scan.Match is { } game)
        {
            if (game.Pid == _failedPid && Environment.TickCount64 < _retryAt) return;
            _log.LogInformation("detected {profile} (pid {pid}, comm {comm})", game.Profile.Name, game.Pid, game.Comm);
            await StartAsync(game.Profile, game.Pid, SteamLibrary.AppIdOfProcess(game.Pid), unconfirmed: false, ct);
            return;
        }

        var launch = _steam.Decide(scan.Launches, _cfg, Environment.TickCount64, AppName, id => _seen.Find(id)?.Profile is { } owner && _cfg.FindProfile(owner) is not null);
        foreach (var l in _steam.New)
        {
            if (AppName(l.AppId) is not { } name || SteamLibrary.Tools.Contains(l.AppId) || SteamLibrary.IsToolName(name)) continue;
            _seen.Touch(l.AppId, name);
            _log.LogInformation("steam launched {name} (app {app}, pid {pid})", name, l.AppId, l.Pid);
        }
        if (launch is null || (launch.Launch.Pid == _failedPid && Environment.TickCount64 < _retryAt)) return;

        if (launch.Profile is { } profile)
        {
            _log.LogInformation("detected {profile} (steam app {app}, pid {pid})", profile.Name, launch.Launch.AppId, launch.Launch.Pid);
            await StartAsync(profile, launch.Launch.Pid, launch.Launch.AppId, unconfirmed: false, ct, byLaunch: true);
        }
        else
        {
            _log.LogInformation("detected unconfirmed game {name} (steam app {app}, pid {pid})", launch.Name, launch.Launch.AppId, launch.Launch.Pid);
            var ok = await StartAsync(_cfg.AutoProfile((int)launch.Launch.AppId, launch.Name), launch.Launch.Pid, launch.Launch.AppId, unconfirmed: true, ct, byLaunch: true);
            if (!ok) _steam.MarkStale(launch.Launch.Pid);   // one error tone is enough for a game nobody set up
        }
    }

    private async Task<bool> StartAsync(ProfileConfig profile, int pid, long? steamAppId, bool unconfirmed, CancellationToken ct, bool byLaunch = false)
    {
        var r = await _rec.StartAsync(profile, "auto", pid, ct, steamAppId, unconfirmed);
        if (r.Ok) _started = (_rec.ActiveSessionId, pid, steamAppId, byLaunch);
        // A profile that matched by process and turns out to be a Steam app: remember the pair, so the next launch is not taken for an unconfirmed game.
        if (r.Ok && !unconfirmed && steamAppId is long app && (profile.Match ?? profile.MatchCmdline) is not null) _seen.Link(app, AppName(app) ?? profile.Name, profile.Name);
        if (r.Ok) return true;
        // Do not hammer (and beep) once a second: log it once, retry in a minute.
        _log.LogError("could not start a session for {profile}: {msg}{retry}", profile.Name, r.Message, unconfirmed ? "" : " (retrying in 60 s)");
        _failedPid = pid; _retryAt = Environment.TickCount64 + 60_000;
        return false;
    }

    /// <summary>Manifest lookups are file reads: remember hits for good and misses for a minute.</summary>
    private string? AppName(long appId)
    {
        var now = Environment.TickCount64;
        if (_names.TryGetValue(appId, out var c) && (c.Name is not null || now - c.At < 60_000)) return c.Name;
        var name = SteamLibrary.AppName(appId);
        _names[appId] = (name, now);
        return name;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await _rec.DisposeAsync();
    }
}
