using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kairec.Protocol;

namespace Kairec.Daemon;

/// <summary>Unix socket the CLI talks to. One JSON request per connection, one JSON reply.</summary>
public sealed class ControlSocketServer : BackgroundService
{
    private readonly RecordingCoordinator _rec;
    private readonly Core.Config.KairecConfig _cfg;
    private readonly ConfigService _configService;
    private readonly GsiService _gsi;
    private readonly PauseControl _pause;
    private readonly ILogger<ControlSocketServer> _log;

    public ControlSocketServer(RecordingCoordinator rec, Core.Config.KairecConfig cfg, ConfigService configService, GsiService gsi, PauseControl pause, ILogger<ControlSocketServer> log)
    {
        _rec = rec; _cfg = cfg; _configService = configService; _gsi = gsi; _pause = pause; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var path = ControlPaths.SocketPath();
        if (File.Exists(path)) File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(16);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _log.LogInformation("control socket at {path}", path);
        ct.Register(() => { try { listener.Close(); } catch { } });

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(client, ct);
        }
        try { File.Delete(path); } catch { }
    }

    private async Task HandleAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            ControlResponse resp;
            try
            {
                var line = await ReadLineAsync(client, ct);
                var req = JsonSerializer.Deserialize(line, ProtocolJson.Default.ControlRequest) ?? new ControlRequest();
                resp = await DispatchAsync(req, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "control request failed");
                resp = new ControlResponse { Ok = false, Message = ex.Message };
            }
            var json = JsonSerializer.Serialize(resp, ProtocolJson.Default.ControlResponse) + "\n";
            try { await client.SendAsync(Encoding.UTF8.GetBytes(json), ct); } catch { }
        }
    }

    private async Task<ControlResponse> DispatchAsync(ControlRequest req, CancellationToken ct)
    {
        switch (req.Cmd)
        {
            case "mark": return await _rec.MarkAsync(req.Label, ct);
            case "save": return await _rec.SaveAsync(req.Seconds, req.Label, ct);
            case "stop": return await _rec.StopAsync("manual", ct);
            case "start":
            {
                var name = req.Profile ?? _cfg.Profiles.FirstOrDefault()?.Name;
                var profile = name is null ? null : _cfg.FindProfile(name);
                if (profile is null) return new ControlResponse { Ok = false, Message = $"unknown profile '{name}'. Known: {string.Join(", ", _cfg.Profiles.Select(p => p.Name))}" };
                return await _rec.StartAsync(profile, "manual", null, ct);
            }
            case "pause":
            {
                var msg = _pause.Pause(req.Mode ?? "session", req.Hours, _rec.ActiveGamePid, _rec.Status().Profile);
                var stopped = _rec.IsActive ? await _rec.StopAsync("paused", ct) : null;
                return new ControlResponse { Ok = true, Message = $"recording paused {msg}" + (stopped is { Ok: true } ? "; current session stopped" : "") };
            }
            case "resume":
                _pause.Resume();
                return new ControlResponse { Ok = true, Message = "recording on; a running game will be picked up within a second" };
            case "reload":
            {
                try
                {
                    var r = _configService.ReloadFromDisk(_rec.IsActive);
                    _gsi.EnsureConfigFile();
                    return new ControlResponse { Ok = true, Message = r == "applied" ? "config reloaded" : "config reloaded; applies when the current session ends" };
                }
                catch (Exception ex) { return new ControlResponse { Ok = false, Message = "config error: " + ex.Message }; }
            }
            case "config":
                return new ControlResponse { Ok = true, Message = _configService.Path };
            case "status":
            {
                var s = _rec.Status();
                s.GsiLastSeconds = _gsi.SecondsSinceLastPayload; s.GsiMap = _gsi.CurrentMap;
                var ps = _pause.Current; s.Paused = ps.Mode == "off" ? null : PauseControl.Describe(ps);
                var gsiNote = s.GsiLastSeconds is double g ? $", gsi {g:0}s ago" + (s.GsiMap is null ? "" : $" on {s.GsiMap}") : "";
                var msg = s.State == "idle" ? "idle" + (s.Paused is null ? "" : $" (auto recording paused {s.Paused})") + gsiNote
                    : $"{s.State}: {s.Profile} ({s.Mode}) {s.ElapsedSeconds:0}s elapsed"
                      + (s.WindowSeconds is double w ? $", {w:0}s in window" : "")
                      + $", {s.MarkerCount} markers" + (s.PendingMarks > 0 ? $" ({s.PendingMarks} pending clip)" : "")
                      + gsiNote + $", {s.SessionDir}";
                return new ControlResponse { Ok = true, Message = msg, Status = s };
            }
            default: return new ControlResponse { Ok = false, Message = $"unknown command '{req.Cmd}'" };
        }
    }

    private static async Task<string> ReadLineAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();
        while (true)
        {
            var n = await s.ReceiveAsync(buf, ct);
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (sb.ToString().Contains('\n')) break;
        }
        return sb.ToString().Split('\n')[0];
    }
}
