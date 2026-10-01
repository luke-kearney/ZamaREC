using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ZamaRec.Core.Recording;

/// <summary>One running gpu-screen-recorder process, driven over its -ipc JSON socket.</summary>
public sealed class GsrProcess : IRecorder
{
    private readonly Process _proc;
    private readonly string _socketPath;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _ipcLock = new(1, 1);
    private int _nextId = 1;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Pid => _proc.Id;
    public bool HasExited => _proc.HasExited;
    public Task Exited => _exited.Task;
    public string Executable { get; }

    private GsrProcess(Process proc, string socketPath, string exe, ILogger log)
    {
        _proc = proc; _socketPath = socketPath; Executable = exe; _log = log;
        proc.Exited += (_, _) => _exited.TrySetResult();
        try { if (proc.HasExited) _exited.TrySetResult(); } catch (InvalidOperationException) { }
    }

    public static GsrProcess Start(IEnumerable<string> args, string socketPath, ILogger log, string executable = "gpu-screen-recorder")
    {
        if (File.Exists(socketPath)) File.Delete(socketPath);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        log.LogInformation("spawning {exe} {args}", executable, string.Join(' ', psi.ArgumentList.Select(Quote)));
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log.LogDebug("gsr: {line}", e.Data); };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // "failed to write frame ... to muxer" is the recorder flushing its last packet after we asked it to stop; harmless.
            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) && !e.Data.Contains("failed to write frame")) log.LogWarning("gsr: {line}", e.Data);
            else log.LogDebug("gsr: {line}", e.Data);
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return new GsrProcess(proc, socketPath, executable, log);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>Waits until the recorder has created its socket (it does so after initialising capture).</summary>
    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_proc.HasExited) throw new InvalidOperationException($"gpu-screen-recorder exited early with code {_proc.ExitCode}");
            if (File.Exists(_socketPath)) return;
            await Task.Delay(50, ct);
        }
        throw new TimeoutException("gpu-screen-recorder did not create its IPC socket in time");
    }

    // --- IPC requests (names from the man page) ---

    /// <summary>Record mode: stop and save. Replay/hybrid: stop without saving the buffer. Returns the saved path in record mode.</summary>
    public Task<IpcReply> StopAsync(CancellationToken ct) => SendAsync("stop", null, ct);

    public Task<IpcReply> SaveReplayAsync(int? seconds, CancellationToken ct) =>
        SendAsync("save-replay", seconds is null ? null : new IpcData { Seconds = seconds }, ct);

    public Task<IpcReply> StartReplayRecordingAsync(CancellationToken ct) => SendAsync("start-replay-recording", null, ct);

    public Task<IpcReply> StopReplayRecordingAsync(CancellationToken ct) => SendAsync("stop-replay-recording", null, ct);

    private async Task<IpcReply> SendAsync(string name, IpcData? data, CancellationToken ct)
    {
        await _ipcLock.WaitAsync(ct);
        try
        {
            var req = new IpcRequest { Id = _nextId++, Name = name, Data = data };
            var json = JsonSerializer.Serialize(req, GsrIpcJson.Default.IpcRequest) + "\n";
            _log.LogDebug("gsr ipc -> {json}", json.TrimEnd());
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct);
            await sock.SendAsync(Encoding.UTF8.GetBytes(json), ct);
            var buf = new byte[4096];
            var sb = new StringBuilder();
            while (true)
            {
                var n = await sock.ReceiveAsync(buf, ct);
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().Contains('\n')) break;
            }
            var line = sb.ToString().Split('\n')[0];
            _log.LogDebug("gsr ipc <- {json}", line);
            var reply = JsonSerializer.Deserialize(line, GsrIpcJson.Default.IpcReply) ?? throw new InvalidDataException("empty gsr reply");
            if (!reply.Ok) _log.LogWarning("gsr {name} failed: {err}", name, reply.Data);
            return reply;
        }
        finally { _ipcLock.Release(); }
    }

    /// <summary>Waits for a clean exit, escalating to SIGINT and then SIGKILL.</summary>
    public async Task WaitForExitAsync(TimeSpan grace, CancellationToken ct)
    {
        if (await WaitAsync(grace)) return;
        _log.LogWarning("gsr did not exit after stop; sending SIGINT");
        TrySignal(2);
        if (await WaitAsync(TimeSpan.FromSeconds(10))) return;
        _log.LogError("gsr still running; killing");
        try { _proc.Kill(); } catch { }
        await WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<bool> WaitAsync(TimeSpan t)
    {
        var done = await Task.WhenAny(_exited.Task, Task.Delay(t));
        return done == _exited.Task;
    }

    private void TrySignal(int sig)
    {
        try { Process.Start("kill", $"-{sig} {_proc.Id}")?.WaitForExit(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_proc.HasExited)
        {
            TrySignal(2);
            await WaitAsync(TimeSpan.FromSeconds(10));
            if (!_proc.HasExited) { try { _proc.Kill(); } catch { } }
        }
        _proc.Dispose();
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
    }
}

public sealed class IpcRequest
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("data")] public IpcData? Data { get; set; }
}

public sealed class IpcData
{
    [JsonPropertyName("seconds")] public int? Seconds { get; set; }
    [JsonPropertyName("restart-replay")] public bool? RestartReplay { get; set; }
}

public sealed class IpcReply
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    /// <summary>Saved file path on success for stop/save-replay/stop-replay-recording, error text on failure.</summary>
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonIgnore] public bool Ok => Result == "ok";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcReply))]
public partial class GsrIpcJson : JsonSerializerContext { }
