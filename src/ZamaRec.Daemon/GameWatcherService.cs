using ZamaRec.Core.Config;
using ZamaRec.Core.Processes;

namespace ZamaRec.Daemon;

/// <summary>
/// Starts a session when a profile's process appears, or when Steam launches a game (one a profile claims by
/// app id, or an unconfirmed one recorded with the [auto_games] defaults). Stops it when that process exits.
/// Also makes the choice for a start by hand (StartByHandAsync): the game that is running, else the desktop.
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
    /// <summary>Game processes not to start a session for: stopped by hand, or running when a desktop recording began. Until they exit.</summary>
    private readonly HashSet<int> _leaveAlone = new();
    /// <summary>The loop and a start by hand both read and change the state above.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
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
            try { await _gate.WaitAsync(ct); } catch (OperationCanceledException) { break; }
            try
            {
                await _rec.TickAsync(ct);
                if (_cfg.Version != _watcherVersion) { _watcher = new ProcessWatcher(_cfg.Profiles, _log); _watcherVersion = _cfg.Version; }
                NoteSessionEnd();   // one stopped from outside since the last pass

                if (_rec.IsActive)
                {
                    if (_rec.RecorderDied && _rec.ActiveSeconds < RecordingCoordinator.EarlyDeathSeconds)
                    {
                        // Ready, then gone: as good as a failed start (a bad audio source does this). Same 60 s wait, one tone.
                        var pid = _rec.ActiveGamePid;
                        _log.LogError("recorder exited {s:0.0} s after starting{why}; closing session{retry}", _rec.ActiveSeconds,
                            _rec.RecorderError is { } why ? $" ({why})" : "", pid is null ? "" : " (retrying in 60 s)");
                        await _rec.StopAsync("recorder failed at start", ct, failed: true);
                        if (pid is int p) { _failedPid = p; _retryAt = Environment.TickCount64 + 60_000; }
                    }
                    else if (_rec.RecorderDied)
                    {
                        _log.LogWarning("recorder exited unexpectedly{why}; closing session", _rec.RecorderError is { } why ? $" ({why})" : "");
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
                    else if (_rec.ActiveIsDesktop && _cfg.Desktop.YieldToGames && !_pause.IsPaused(ProcessWatcher.IsAlive))
                        await ScanAsync(self, ct, first: () => _rec.StopAsync("a game started", ct));
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
            finally { _gate.Release(); }
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
            // A game stopped by hand stays stopped until it exits or you start it again.
            if (_rec.LastStop is { Reason: "manual", GamePid: int pid } && ProcessWatcher.IsAlive(pid)) _leaveAlone.Add(pid);
        }
        _wasActive = id is not null; _lastSessionId = id;
    }

    /// <param name="first">Runs before a session is started (ending a desktop recording); not at all if nothing is.</param>
    private async Task ScanAsync(int self, CancellationToken ct, Func<Task>? first = null)
    {
        _leaveAlone.RemoveWhere(pid => !ProcessWatcher.IsAlive(pid));
        var scan = _watcher.Scan(self);
        if (scan.Match is { } game && !_leaveAlone.Contains(game.Pid))
        {
            if (game.Pid == _failedPid && Environment.TickCount64 < _retryAt) return;
            _log.LogInformation("detected {profile} (pid {pid}, comm {comm})", game.Profile.Name, game.Pid, game.Comm);
            if (first is not null) await first();
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

        if (first is not null) await first();
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

    /// <summary>
    /// A start by hand (the start key, the tray, the deck). With no profile named: the game that is running if
    /// any (a profile's process, else a Steam launch), tied to its process like an automatic start, else a desktop
    /// recording. A named profile is tied to its game when that is running. Works while recording is turned off,
    /// and ignores the 60 s wait after a failed start: you asked for it.
    /// </summary>
    public async Task<ZamaRec.Protocol.ControlResponse> StartByHandAsync(ProfileConfig? profile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_rec.IsActive) return new ZamaRec.Protocol.ControlResponse { Ok = false, Message = $"already recording profile '{_rec.Status().Profile}'" };
            var self = Environment.ProcessId;
            var scan = _watcher.Scan(self);
            var desktop = profile is not null && string.Equals(profile.Name, DesktopConfig.ProfileName, StringComparison.OrdinalIgnoreCase);
            (ProfileConfig Profile, int Pid, long? AppId, bool ByLaunch, bool Unconfirmed)? pick = null;
            if (!desktop)
            {
                if (scan.Match is { } game && (profile is null || Same(game.Profile, profile)))
                    pick = (game.Profile, game.Pid, SteamLibrary.AppIdOfProcess(game.Pid), false, false);
                else foreach (var l in scan.Launches)
                {
                    if (_cfg.ProfileForSteamApp(l.AppId) is { } owner) { if (profile is null || Same(owner, profile)) { pick = (owner, l.Pid, l.AppId, true, false); break; } continue; }
                    if (profile is not null || _cfg.AutoGames.Ignores(l.AppId) || l.AppId > int.MaxValue) continue;
                    if (_seen.Find(l.AppId)?.Profile is { } claimed && _cfg.FindProfile(claimed) is not null) continue;   // its profile's own process decides
                    if (AppName(l.AppId) is not { } name || SteamLibrary.IsToolName(name)) continue;
                    pick = _cfg.FindProfile(name) is { } named ? (named, l.Pid, l.AppId, true, false) : (_cfg.AutoProfile((int)l.AppId, name), l.Pid, l.AppId, true, true);
                    break;
                }
            }
            if (pick is { } p)
            {
                _leaveAlone.Remove(p.Pid);
                _log.LogInformation("start by hand: {profile} is running (pid {pid})", p.Profile.Name, p.Pid);
                return await StartAsync(p.Profile, p.Pid, p.AppId, p.Unconfirmed, ct, p.ByLaunch, "manual");
            }
            profile ??= _cfg.DesktopProfile();
            if (string.Equals(profile.Name, DesktopConfig.ProfileName, StringComparison.OrdinalIgnoreCase))
            {
                // What runs now is not what a desktop recording should give way to; a game started from here on is.
                if (scan.Match is { } running) _leaveAlone.Add(running.Pid);
                _steam.MarkStale(scan.Launches);
            }
            return await _rec.StartAsync(profile, "manual", null, ct);
        }
        finally { _gate.Release(); }
    }

    private static bool Same(ProfileConfig a, ProfileConfig b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> StartAsync(ProfileConfig profile, int pid, long? steamAppId, bool unconfirmed, CancellationToken ct, bool byLaunch = false) =>
        (await StartAsync(profile, pid, steamAppId, unconfirmed, ct, byLaunch, "auto")).Ok;

    private async Task<ZamaRec.Protocol.ControlResponse> StartAsync(ProfileConfig profile, int pid, long? steamAppId, bool unconfirmed, CancellationToken ct, bool byLaunch, string trigger)
    {
        var r = await _rec.StartAsync(profile, trigger, pid, ct, steamAppId, unconfirmed);
        if (r.Ok) _started = (_rec.ActiveSessionId, pid, steamAppId, byLaunch);
        // A profile that matched by process and turns out to be a Steam app: remember the pair, so the next launch is not taken for an unconfirmed game.
        if (r.Ok && !unconfirmed && steamAppId is long app && (profile.Match ?? profile.MatchCmdline) is not null) _seen.Link(app, AppName(app) ?? profile.Name, profile.Name);
        if (r.Ok) return r;
        // Do not hammer (and beep) once a second: log it once, retry in a minute.
        _log.LogError("could not start a session for {profile}: {msg}{retry}", profile.Name, r.Message, unconfirmed ? "" : " (retrying in 60 s)");
        _failedPid = pid; _retryAt = Environment.TickCount64 + 60_000;
        return r;
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
