using Kairec.Core.Config;
using Kairec.Core.Processes;

namespace Kairec.Daemon;

/// <summary>Starts a session when a profile's process appears, stops it when that process exits.</summary>
public sealed class GameWatcherService : BackgroundService
{
    private readonly KairecConfig _cfg;
    private readonly RecordingCoordinator _rec;
    private readonly ILogger<GameWatcherService> _log;
    private ProcessWatcher _watcher;
    private int _watcherVersion;
    private int _failedPid; private long _retryAt;

    public GameWatcherService(KairecConfig cfg, RecordingCoordinator rec, ILogger<GameWatcherService> log)
    {
        _cfg = cfg; _rec = rec; _log = log;
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
                }
                else
                {
                    if (_cfg.Version != _watcherVersion) { _watcher = new ProcessWatcher(_cfg.Profiles, _log); _watcherVersion = _cfg.Version; }
                    var game = _watcher.FindRunning(self);
                    if (game is not null && !(game.Pid == _failedPid && Environment.TickCount64 < _retryAt))
                    {
                        _log.LogInformation("detected {profile} (pid {pid}, comm {comm})", game.Profile.Name, game.Pid, game.Comm);
                        var r = await _rec.StartAsync(game.Profile, "auto", game.Pid, ct);
                        if (!r.Ok)
                        {
                            // Do not hammer (and beep) once a second: log it once, retry in a minute.
                            _log.LogError("could not start a session for {profile}: {msg} (retrying in 60 s)", game.Profile.Name, r.Message);
                            _failedPid = game.Pid; _retryAt = Environment.TickCount64 + 60_000;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "watcher loop error"); }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await _rec.DisposeAsync();
    }
}
