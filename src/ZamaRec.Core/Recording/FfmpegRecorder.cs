using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ZamaRec.Core.Config;
using Microsoft.Extensions.Logging;

namespace ZamaRec.Core.Recording;

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
/// Nothing here asks anything of the GPU: ffmpeg's progress lines are read as they come. The first one says
/// frames are flowing; after that they are only counted, for a line in the log every half minute with the frame
/// rate the recorder really ran at (<see cref="FfmpegProgress"/>).
///
/// Video routes. How ddagrab's frame reaches the encoder differs by GPU vendor (<see cref="Routes"/>), and for AMD
/// more than one way is known; <see cref="StartReadyAsync"/> tries them in order and remembers the one that works.
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
    private readonly FfmpegProgress _progress = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    private readonly Stopwatch _running = Stopwatch.StartNew();
    private bool _stopping;

    /// <summary>One way of getting ddagrab's D3D11 BGRA frame to the encoder: the filters that follow ddagrab.</summary>
    public sealed record VideoRoute(string Name, string Filters, bool OnGpu);

    /// <summary>
    /// The routes for a GPU vendor, best first. NVENC takes ddagrab's BGRA textures as they are. AMF refuses them
    /// (SubmitInput() failed with error 18, AMF_DIRECTX_FAILED, on the first AMD machine tried), so the frame is
    /// first converted to NV12 on the GPU: by Direct3D 11's video processor, or failing that by AMF's own converter.
    /// Downloading the frame and converting it on the CPU is the last resort (it is work per frame while a game
    /// runs) and all there is for Quick Sync so far.
    /// </summary>
    public static IReadOnlyList<VideoRoute> Routes(string gpu) => gpu switch
    {
        "amd" =>
        [
            new("d3d11 nv12", ",scale_d3d11=format=nv12", true),
            new("amf nv12", ",hwmap=derive_device=amf,vpp_amf=format=nv12", true),
            new("system memory", MemoryFilters, false),
        ],
        "intel" => [new("system memory", MemoryFilters, false)],
        _ => [new("d3d11 bgra", "", true)],
    };

    private const string MemoryFilters = ",hwdownload,format=bgra,format=nv12";
    private static readonly Dictionary<string, int> KnownRoute = new();   // vendor -> index of the route that worked last time

    /// <summary>Starts ffmpeg and waits until it is capturing, going down the vendor's routes until one starts.</summary>
    public static async Task<FfmpegRecorder> StartReadyAsync(ZamaRecConfig cfg, EffectiveProfile p, string mediaDir, ILogger log, TimeSpan readyTimeout, CancellationToken ct)
    {
        // gfxcapture is the default but has run on one machine only; where it does not start (an older Windows,
        // another vendor's route) the recording is made with ddagrab, and it is not tried again until the daemon restarts.
        if (!IsGfx(cfg.Capture.Grabber) || _gfxFailed) return await StartRoutesAsync(cfg, p, mediaDir, log, readyTimeout, _gfxFailed ? "ddagrab" : null, ct);
        try { return await StartRoutesAsync(cfg, p, mediaDir, log, readyTimeout, null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _gfxFailed = true;
            log.LogWarning("gfxcapture did not start ({why}); recording with ddagrab", ex.Message);
            return await StartRoutesAsync(cfg, p, mediaDir, log, readyTimeout, "ddagrab", ct);
        }
    }

    private static volatile bool _gfxFailed;
    private static bool IsGfx(string? grabber) => string.Equals(grabber, "gfxcapture", StringComparison.OrdinalIgnoreCase);

    private static async Task<FfmpegRecorder> StartRoutesAsync(ZamaRecConfig cfg, EffectiveProfile p, string mediaDir, ILogger log, TimeSpan readyTimeout, string? grabber, CancellationToken ct)
    {
        var gpu = HwCodec.Normalize(cfg.Capture.Gpu);
        var routes = Routes(gpu);
        int first;
        lock (KnownRoute) first = KnownRoute.GetValueOrDefault(gpu);
        for (var i = first; ; i++)
        {
            var route = routes[i];
            var rec = Start(cfg, p, mediaDir, log, route, grabber);
            try
            {
                await rec.WaitForReadyAsync(readyTimeout, ct);
                lock (KnownRoute) KnownRoute[gpu] = i;
                rec.RaiseGpuPriority();
                if (route.OnGpu) log.LogInformation("video route: {route} (the frame stays on the GPU)", route.Name);
                else log.LogWarning("video route: {route} (every frame is copied to system memory and converted on the CPU)", route.Name);
                return rec;
            }
            catch (Exception ex)
            {
                await rec.DisposeAsync();
                if (ex is OperationCanceledException || i + 1 >= routes.Count) throw;
                log.LogWarning("video route {route} did not start ({why}); trying {next}", route.Name, ex.Message, routes[i + 1].Name);
            }
        }
    }

    /// <summary>
    /// Asks Windows' GPU scheduler, once, to serve ffmpeg ahead of normal processes. A game that uses the whole
    /// GPU otherwise leaves the capture and the encoder waiting: in a GPU-bound game at ~90 fps ddagrab got
    /// 40 to 57 frames a second and the recording juddered, while the same game capped at 60 recorded smoothly
    /// (2026-10-05). It only works once ffmpeg has its D3D device (before that the call is "invalid parameter"),
    /// hence after the first frame. Not a GPU query and never repeated.
    /// </summary>
    private void RaiseGpuPriority()
    {
        // D3DKMT_SCHEDULINGPRIORITYCLASS: idle 0, below normal 1, normal 2, above normal 3, high 4, realtime 5.
        // High changed nothing on the owner's machine (still 41-52 frames a second from a GPU-bound game);
        // realtime is what OBS asks for, and another recorder's notes report none dropped at realtime against
        // 54 of 484 at normal on a saturated GPU. On trial.
        const int Realtime = 5;
        try
        {
            var status = D3DKMTSetProcessSchedulingPriorityClass(_proc.Handle, Realtime);
            if (status == 0) _log.LogInformation("recorder GPU priority: realtime");
            else _log.LogWarning("recorder GPU priority not raised (status 0x{status:x8}); a game using the whole GPU may starve the capture", status);
        }
        catch (Exception ex) { _log.LogWarning("recorder GPU priority not raised: {why}", ex.Message); }
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

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

    public static FfmpegRecorder Start(ZamaRecConfig cfg, EffectiveProfile p, string mediaDir, ILogger log, VideoRoute? route = null, string? grabber = null)
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
            var pipe = AudioPipe.TryOpen(src, $"zamarec-{Environment.ProcessId}-a{pipes.Count}", baseReal, log);
            if (pipe is null) continue;
            pipeOf[src] = pipes.Count;
            pipes.Add(pipe);
        }

        var args = new List<string>
        {
            // level+info: each line says how serious it is, and at info ffmpeg describes what it captures and encodes (size, pixel format, rate)
            "-hide_banner", "-loglevel", "level+info", "-nostats", "-progress", "pipe:1", "-stats_period", "0.25",
            "-init_hw_device", "d3d11va",   // the one hardware device: ddagrab and the encoder pick it up as the default
        };
        foreach (var pipe in pipes)
        {
            args.AddRange(["-thread_queue_size", "4096", "-f", pipe.FfmpegFormat, "-ar", pipe.SampleRate.ToString(), "-ac", pipe.Channels.ToString()]);
            if (pipe.Channels is 1 or 2) args.AddRange(["-ch_layout", pipe.Channels == 1 ? "mono" : "stereo"]);
            args.AddRange(["-i", pipe.Path]);
        }

        var graph = new StringBuilder();
        // capture.grab_fps above the recording's rate: ddagrab then hands over the game's own frames as they
        // are presented, and the recording keeps the first one in each 1/fps slot WITH the time it arrived
        // (variable frame rate, see below). Putting them on an even 1/fps grid instead shows a game running
        // at 90 fps as steps of one and two game frames, which judders; Steam's recorder keeps true times too
        // (its 60 fps files of such a game have gaps of ~12 and ~23 ms).
        //
        // capture.grabber = "gfxcapture" (the default): Windows.Graphics.Capture instead of Desktop Duplication.
        // It always works this way: its frames come as the compositor shows them, each with its own display
        // time (pts from 0, on the monitor's refresh grid, a constant offset from the wall clock when
        // measured on the desktop). ddagrab in this mode got only ~40 frames a second out of a GPU-bound game.
        var gfx = IsGfx(grabber ?? c.Grabber);
        var grabFps = gfx ? (c.GrabFps > p.Fps ? c.GrabFps : p.Fps * 4) : Math.Max(p.Fps, c.GrabFps);
        var trueTimes = grabFps > p.Fps;
        if (trueTimes) log.LogInformation("grabbing the screen with {grabber} at up to {grab} fps for a recording of up to {fps} fps, frames keep their own times", gfx ? "gfxcapture" : "ddagrab", grabFps, p.Fps);
        if (gfx) graph.Append($"gfxcapture=monitor_idx={MonitorIndex(c.Monitor, log)}:capture_cursor={(c.Cursor ? 1 : 0)}:max_framerate={grabFps}");
        else graph.Append($"ddagrab=output_idx={MonitorIndex(c.Monitor, log)}:framerate={grabFps}:draw_mouse={(c.Cursor ? 1 : 0)}");
        // dup_frames=0: ddagrab waits for the next presented frame instead of handing the last one out again
        // on its own timer. That timer is Windows' 15.6 ms tick, and with it the stamps were the tick's, not
        // the frames' (gaps mostly 16 ms in a 90 fps game, where Steam's show 12 and 23). A still screen
        // then gives no video frames at all until something moves.
        if (trueTimes && !gfx) graph.Append(":dup_frames=0");
        graph.Append((route ?? Routes(gpu)[0]).Filters);
        // Every frame is stamped with the wall clock (media time = clock - B, as on Linux) and the encoder
        // side runs constant rate (-fps_mode cfr below): ffmpeg puts each frame in the nearest 1/fps slot,
        // repeats one where ddagrab delivered none and drops one that came early. The stamps alone carry
        // ddagrab's arrival jitter (0 ms / 33 ms pairs ten times a second), and the encoder counts in 1/fps
        // whatever the filter's timebase, so passed through as they are they collide ("Non-monotonic DTS")
        // and HLS playback hitches. Counting frames instead (PREV_OUTPTS + 1/fps) only holds if ddagrab
        // delivers the full rate, and it gives ~57 of 60: media time ran 5% slow and jumped half a second
        // every ten. settb so the stamps keep their microseconds up to the slotting.
        var wall = $"(RTCTIME-{baseReal.ToString(CultureInfo.InvariantCulture)})/(1000000*TB)";
        // gfxcapture: keep its own display times and only move them onto the clock, by the offset found at frame 0
        // (st/ld keep a value between frames).
        graph.Append(gfx ? $",settb=AVTB,setpts='if(eq(N,0),st(0,{wall}-PTS));PTS+ld(0)'" : $",settb=AVTB,setpts={wall}");
        if (trueTimes) graph.Append($",select='isnan(prev_selected_t)+gt(floor(t*{p.Fps}),floor(prev_selected_t*{p.Fps}))'");
        graph.Append("[v]");
        if (!string.IsNullOrWhiteSpace(p.Scale)) log.LogWarning("scale {scale} is ignored on Windows: the capture is recorded at the monitor's size", p.Scale);

        // Audio mixing goes into its OWN -filter_complex: a graph that holds both ddagrab and pipe-fed audio
        // inputs is scheduled as one unit, keeps waiting on the pipes, and polls ddagrab a handful of times a
        // second -- the recording comes out at ~5 fps with nothing reported dropped. Separate graphs run free.
        var audioGraph = new StringBuilder();
        var maps = new List<string> { "-map", "[v]" };
        var ti = 0;
        foreach (var track in tracks)
        {
            var inputs = track.Sources.Where(pipeOf.ContainsKey).Select(s => pipeOf[s]).Distinct().ToList();
            if (inputs.Count == 0) continue;
            if (inputs.Count == 1) maps.AddRange(["-map", $"{inputs[0]}:a"]);
            else
            {
                if (audioGraph.Length > 0) audioGraph.Append(';');
                audioGraph.Append(string.Concat(inputs.Select(i => $"[{i}:a]"))).Append($"amix=inputs={inputs.Count}:normalize=0[t{ti}]");
                maps.AddRange(["-map", $"[t{ti}]"]);
            }
            ti++;
        }
        args.AddRange(["-filter_complex", graph.ToString()]);
        if (audioGraph.Length > 0) args.AddRange(["-filter_complex", audioGraph.ToString()]);
        args.AddRange(maps);

        // The encoder is told ddagrab's rate (grab_fps) and budgets bitrate/rate per frame; -r cannot correct it
        // (ffmpeg refuses -r outside cfr). Only ~fps frames arrive, so without scaling a 20000k recording
        // came out at 5000k and blocky. The file's real bitrate is the configured one.
        var kbps = trueTimes ? (int)((long)p.BitrateKbps * grabFps / p.Fps) : p.BitrateKbps;
        var gop = Math.Max(1, (int)Math.Round(p.Fps * p.KeyframeSeconds));
        args.AddRange(["-c:v", HwCodec.Encoder(p.Codec, gpu)]);
        // true times: the encoder must count finer than 1/fps, or two frames a millisecond apart across a slot boundary collide
        args.AddRange(trueTimes ? ["-fps_mode", "passthrough", "-enc_time_base", "1:90000"] : ["-r", p.Fps.ToString(), "-fps_mode", "cfr"]);
        args.AddRange(["-g", gop.ToString(), "-keyint_min", gop.ToString()]);
        args.AddRange(gpu switch
        {
            "amd" => ["-rc", "cbr", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-usage", "transcoding", "-quality", "speed"],
            "intel" => ["-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-preset", "veryfast"],
            _ => ["-preset", "p4", "-tune", "hq", "-rc", "cbr", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-forced-idr", "1", "-no-scenecut", "1"],
        });
        if (ti > 0) args.AddRange(["-c:a", "aac", "-b:a", "160k"]);

        // The same HLS layout as the Linux build: N-second MPEG-TS segments, a rolling playlist, ffmpeg deleting
        // what falls out of the window. Buffer mode is a timeline here too (see ZamaRecConfig.Effective).
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
        if (_progress.Line(line, _running.Elapsed) is { } report) _log.LogInformation("recorder: {report}", report);
        // The first block that reports frames means capture and encoding are up.
        if (!_ready.Task.IsCompleted && line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var frames) && frames > 0)
        {
            try { File.WriteAllText(_stampFile, $"# zamarec ffmpeg: media time 0 on the monotonic and realtime clocks (us)\n{_baseMonoUs} {_baseRealUs}\n"); }
            catch (Exception ex) { _log.LogWarning(ex, "could not write {file}", _stampFile); }
            _ready.TrySetResult();
        }
    }

    private void OnStderr(string? line)
    {
        if (line is null) return;
        if (_stopping) { _log.LogDebug("ffmpeg: {line}", line); return; }
        if (!IsComplaint(line))
        {
            // what ffmpeg says about its inputs and outputs is worth keeping; a line for every segment it opens is not
            if (!line.Contains("] Opening '", StringComparison.Ordinal)) _log.LogInformation("ffmpeg: {line}", line);
            return;
        }
        _log.LogWarning("ffmpeg: {line}", line);
        lock (_lastErrors) { if (_lastErrors.Length < 2000) _lastErrors.Append(line.Replace("[warning] ", "").Replace("[error] ", "").Replace("[fatal] ", "")).Append('\n'); }
    }

    /// <summary>With -loglevel level+info every line carries its level; anything from warning up is a complaint.</summary>
    public static bool IsComplaint(string line) =>
        line.Contains("[warning]", StringComparison.Ordinal) || line.Contains("[error]", StringComparison.Ordinal)
        || line.Contains("[fatal]", StringComparison.Ordinal) || line.Contains("[panic]", StringComparison.Ordinal);

    private void OnExited()
    {
        foreach (var p in _pipes) p.Dispose();
        _exited.TrySetResult();
        if (_ready.Task.IsCompletedSuccessfully) _log.LogInformation("recorder ended (exit code {code}): {summary}", SafeExitCode(), _progress.Summary(_running.Elapsed));
        if (!_ready.Task.IsCompleted)
        {
            string err; lock (_lastErrors) err = _lastErrors.ToString().Trim();
            // The first complaint is the cause; the later ones are consequences ("nothing was written").
            var cause = err.Split('\n').Select(l => l.LastIndexOf("] ") is >= 0 and var at ? l[(at + 2)..] : l).FirstOrDefault(l => !l.StartsWith("Guessed ", StringComparison.Ordinal));
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
