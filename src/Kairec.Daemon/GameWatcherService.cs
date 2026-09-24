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
                if (_rec.IsActive)
                {
                    await _rec.TickAsync(ct);
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
                    if (game is not null)
                    {
                        _log.LogInformation("detected {profile} (pid {pid}, comm {comm})", game.Profile.Name, game.Pid, game.Comm);
                        await _rec.StartAsync(game.Profile, "auto", game.Pid, ct);
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
