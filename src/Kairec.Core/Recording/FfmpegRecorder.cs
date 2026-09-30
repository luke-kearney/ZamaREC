using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kairec.Core.Config;
using Microsoft.Extensions.Logging;

namespace Kairec.Core.Recording;

/// <summary>
/// The Windows recorder: one ffmpeg process capturing the desktop with Desktop Duplication (ddagrab) straight
/// into the GPU encoder, writing the same HLS timeline the Linux recorder does. Audio comes from WASAPI through
/// named pipes (<see cref="AudioPipe"/>), one per source.
///
/// Time base. Every frame's pts is set from the wall clock relative to one instant B chosen before ffmpeg starts
/// (setpts with RTCTIME), and each audio pipe begins with exactly as much silence as passed between B and its
/// connection. So media time t is simply "B + t" on the clock, whatever ffmpeg's start-up took, and B is what
/// goes into the first-frame file the session reads (the same file gpu-screen-recorder writes on Linux).
///
/// Nothing here runs periodically on the hot path: ffmpeg's progress lines are read as they come and only the
/// first one matters (it says frames are flowing).
/// </summary>
public sealed class FfmpegRecorder : IRecorder
{
    private readonly Process _proc;
    private readonly List<AudioPipe> _pipes;
    private readonly ILogger _log;
    private readonly string _stampFile;
    private readonly long _baseMonoUs, _baseRealUs;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _lastErrors = new();
    private bool _stopping;

    public int Pid => _proc.Id;
    public bool HasExited => _proc.HasExited;
    public Task Exited => _exited.Task;
    public string Executable { get; }

    private FfmpegRecorder(Process proc, List<AudioPipe> pipes, string stampFile, long baseMonoUs, long baseRealUs, ILogger log)
    {
        _proc = proc; _pipes = pipes; _stampFile = stampFile; _baseMonoUs = baseMonoUs; _baseRealUs = baseRealUs; _log = log;
        Executable = proc.StartInfo.FileName;
        proc.Exited += (_, _) => OnExited();
        try { if (proc.HasExited) OnExited(); } catch (InvalidOperationException) { }
    }

    public static FfmpegRecorder Start(KairecConfig cfg, EffectiveProfile p, string mediaDir, ILogger log)
    {
        var c = cfg.Capture;
        var gpu = HwCodec.Normalize(c.Gpu);
        var tracks = c.EffectiveTracks();
        var baseMono = MonotonicClock.NowMicros();
        var baseReal = MonotonicClock.RealtimeMicros();

        // One pipe per distinct source. Sources this recorder cannot capture are left out with a warning, not an error.
        var pipes = new List<AudioPipe>();
        var pipeOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in tracks.SelectMany(t => t.Sources).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pipe = AudioPipe.TryOpen(src, $"kairec-{Environment.ProcessId}-a{pipes.Count}", baseReal, log);
            if (pipe is null) continue;
            pipeOf[src] = pipes.Count;
            pipes.Add(pipe);
        }

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1", "-stats_period", "0.25",
            "-init_hw_device", "d3d11va",   // the one hardware device: ddagrab and the encoder pick it up as the default
        };
        foreach (var pipe in pipes)
        {
            args.AddRange(["-thread_queue_size", "4096", "-f", pipe.FfmpegFormat, "-ar", pipe.SampleRate.ToString(), "-ac", pipe.Channels.ToString()]);
            if (pipe.Channels is 1 or 2) args.AddRange(["-ch_layout", pipe.Channels == 1 ? "mono" : "stereo"]);
            args.AddRange(["-i", pipe.Path]);
        }

        var graph = new StringBuilder();
        graph.Append($"ddagrab=output_idx={MonitorIndex(c.Monitor, log)}:framerate={p.Fps}:draw_mouse={(c.Cursor ? 1 : 0)}");
        if (gpu == "intel") graph.Append(",hwdownload,format=bgra,format=nv12");   // QSV takes system-memory NV12; the conversion is the one CPU step
        graph.Append(",setpts=(RTCTIME-").Append(baseReal.ToString(CultureInfo.InvariantCulture)).Append(")/(1000000*TB)[v]");
        if (!string.IsNullOrWhiteSpace(p.Scale)) log.LogWarning("scale {scale} is ignored on Windows: the capture is recorded at the monitor's size", p.Scale);

        var maps = new List<string> { "-map", "[v]" };
        var ti = 0;
        foreach (var track in tracks)
        {
            var inputs = track.Sources.Where(pipeOf.ContainsKey).Select(s => pipeOf[s]).Distinct().ToList();
            if (inputs.Count == 0) continue;
            if (inputs.Count == 1) maps.AddRange(["-map", $"{inputs[0]}:a"]);
            else
            {
                graph.Append(';').Append(string.Concat(inputs.Select(i => $"[{i}:a]"))).Append($"amix=inputs={inputs.Count}:normalize=0[t{ti}]");
                maps.AddRange(["-map", $"[t{ti}]"]);
            }
            ti++;
        }
        args.AddRange(["-filter_complex", graph.ToString()]);
        args.AddRange(maps);

        var kbps = p.BitrateKbps;
        var gop = Math.Max(1, (int)Math.Round(p.Fps * p.KeyframeSeconds));
        args.AddRange(["-c:v", HwCodec.Encoder(p.Codec, gpu), "-fps_mode", "passthrough", "-g", gop.ToString(), "-keyint_min", gop.ToString()]);
        args.AddRange(gpu switch
        {
            "amd" => ["-rc", "cbr", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-usage", "transcoding", "-quality", "speed"],
            "intel" => ["-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-preset", "veryfast"],
            _ => ["-preset", "p4", "-tune", "hq", "-rc", "cbr", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-forced-idr", "1", "-no-scenecut", "1"],
        });
        if (ti > 0) args.AddRange(["-c:a", "aac", "-b:a", "160k"]);

        // The same HLS layout as the Linux build: N-second MPEG-TS segments, a rolling playlist, ffmpeg deleting
        // what falls out of the window. Buffer mode is a timeline here too (see KairecConfig.Effective).
        var listSize = p.WindowMinutes <= 0 ? 0 : Math.Max(1, p.WindowMinutes * 60 / p.SegmentSeconds);
        args.AddRange(["-f", "hls", "-hls_time", p.SegmentSeconds.ToString(), "-hls_list_size", listSize.ToString(),
            "-hls_flags", "program_date_time" + (listSize > 0 ? "+delete_segments" : ""),
            "-hls_segment_filename", Path.Combine(mediaDir, GsrArguments.SegmentPattern), Path.Combine(mediaDir, GsrArguments.PlaylistName)]);

        var psi = new ProcessStartInfo(Ffmpeg.Exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        log.LogInformation("spawning {exe} {args}", psi.FileName, string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var rec = new FfmpegRecorder(proc, pipes, Path.Combine(mediaDir, GsrArguments.PlaylistName + ".ts"), baseMono, baseReal, log);
        proc.OutputDataReceived += (_, e) => rec.OnProgress(e.Data);
        proc.ErrorDataReceived += (_, e) => rec.OnStderr(e.Data);
        try { proc.Start(); }
        catch { foreach (var pipe in pipes) pipe.Dispose(); throw; }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return rec;
    }

    /// <summary>ddagrab's output index: a number, "screen" for the first, or (a Linux name like DP-1 in a shared config) the first with a note.</summary>
    private static int MonitorIndex(string monitor, ILogger log)
    {
        var m = (monitor ?? "").Trim();
        if (int.TryParse(m, out var idx) && idx >= 0) return idx;
        if (m.Length > 0 && !m.Equals("screen", StringComparison.OrdinalIgnoreCase)) log.LogInformation("monitor '{m}' is not a display index; capturing display 0", m);
        return 0;
    }

    private void OnProgress(string? line)
    {
        if (line is null) return;
        // The first block that reports frames means capture and encoding are up.
        if (!_ready.Task.IsCompleted && line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var frames) && frames > 0)
        {
            try { File.WriteAllText(_stampFile, $"# kairec ffmpeg: media time 0 on the monotonic and realtime clocks (us)\n{_baseMonoUs} {_baseRealUs}\n"); }
            catch (Exception ex) { _log.LogWarning(ex, "could not write {file}", _stampFile); }
            _ready.TrySetResult();
        }
    }

    private void OnStderr(string? line)
    {
        if (line is null) return;
        if (_stopping) { _log.LogDebug("ffmpeg: {line}", line); return; }
        _log.LogWarning("ffmpeg: {line}", line);
        lock (_lastErrors) { if (_lastErrors.Length < 2000) _lastErrors.Append(line).Append('\n'); }
    }

    private void OnExited()
    {
        foreach (var p in _pipes) p.Dispose();
        _exited.TrySetResult();
        if (!_ready.Task.IsCompleted)
        {
            string err; lock (_lastErrors) err = _lastErrors.ToString().Trim();
            // The first complaint is the cause; the later ones are consequences ("nothing was written").
            var cause = err.Split('\n').Select(l => l[(l.LastIndexOf("] ") + 2)..]).FirstOrDefault(l => !l.StartsWith("Guessed ", StringComparison.Ordinal));
            _ready.TrySetException(new InvalidOperationException($"ffmpeg exited with code {SafeExitCode()}" + (cause is { Length: > 0 } ? ": " + cause : "")));
        }
    }

    private int SafeExitCode() { try { return _proc.ExitCode; } catch { return -1; } }

    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var done = await Task.WhenAny(_ready.Task, Task.Delay(timeout, ct));
        if (done != _ready.Task) throw new TimeoutException("ffmpeg did not start capturing in time");
        await _ready.Task;   // rethrows if it died
    }

    public async Task<IpcReply> StopAsync(CancellationToken ct)
    {
        _stopping = true;
        try { await _proc.StandardInput.WriteAsync("q\n"); await _proc.StandardInput.FlushAsync(ct); }
        catch (Exception ex) { _log.LogDebug(ex, "stop: stdin"); }
        return new IpcReply { Result = "ok" };
    }

    public Task<IpcReply> SaveReplayAsync(int? seconds, CancellationToken ct) =>
        Task.FromResult(new IpcReply { Result = "error", Data = "the Windows recorder has no replay buffer; buffer profiles record a timeline" });

    public async Task WaitForExitAsync(TimeSpan grace, CancellationToken ct)
    {
        if (await WaitAsync(grace)) return;
        _log.LogWarning("ffmpeg did not exit after q; killing");
        try { _proc.Kill(); } catch { }
        await WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<bool> WaitAsync(TimeSpan t) => await Task.WhenAny(_exited.Task, Task.Delay(t)) == _exited.Task;

    public async ValueTask DisposeAsync()
    {
        if (!_proc.HasExited)
        {
            await StopAsync(CancellationToken.None);
            await WaitAsync(TimeSpan.FromSeconds(10));
            if (!_proc.HasExited) { try { _proc.Kill(); } catch { } }
        }
        foreach (var p in _pipes) p.Dispose();
        _proc.Dispose();
    }
}
