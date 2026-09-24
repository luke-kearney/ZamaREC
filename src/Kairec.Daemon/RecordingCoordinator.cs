using Kairec.Core;
using Kairec.Core.Config;
using Kairec.Core.Export;
using Kairec.Core.Feedback;
using Kairec.Core.Gsi;
using Kairec.Core.Recording;
using Kairec.Core.Sessions;
using Kairec.Core.Telemetry;
using Kairec.Protocol;

namespace Kairec.Daemon;

/// <summary>
/// The daemon's state machine: idle, or one active session backed by one recorder process.
/// Everything that touches the recorder goes through here, serialised by a single lock.
/// </summary>
public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly KairecConfig _cfg;
    private readonly SoundFeedback _sound;
    private readonly ILogger<RecordingCoordinator> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Active? _active;

    private sealed class Active
    {
        public required EffectiveProfile Profile { get; init; }
        public required SessionStore Session { get; init; }
        public required GsrProcess Recorder { get; init; }
        public required string Trigger { get; init; }
        public int? GamePid { get; init; }
        public SegmentIndex? Segments { get; init; }
        /// <summary>Buffer mode: marks waiting for their "after" time before the buffer is dumped.</summary>
        public MarkJob? Pending { get; set; }
    }

    private sealed class MarkJob
    {
        public long FirstMarkMonoUs;
        public long LastMarkMonoUs;
        public long DueMonoUs;
        public List<Marker> Marks = new();
    }

    private readonly ConfigService _configService;
    private readonly EventLog _events;
    private readonly StatsStore _stats;

    public RecordingCoordinator(KairecConfig cfg, SoundFeedback sound, ConfigService configService, EventLog events, StatsStore stats, ILogger<RecordingCoordinator> log)
    {
        _cfg = cfg; _sound = sound; _configService = configService; _events = events; _stats = stats; _log = log;
    }

    /// <summary>Live footage bytes for the active session (measured for timelines, estimated for buffers).</summary>
    public long ActiveRecordedBytes => _active is null ? 0 : _active.Segments?.TotalBytes ?? EstimateBufferBytes(_active);
    private long EstimateBufferBytes(Active a) => (long)((MonotonicClock.NowMicros() - a.Session.Info.SpawnMonoUs) / 1e6 * _cfg.Capture.BitrateKbps * 125);

    public bool IsActive => _active is not null;
    public string? ActiveSessionId => _active?.Session.Info.Id;
    public string? ActiveSessionDir => _active?.Session.Dir;
    /// <summary>Lets the web API read the live session's markers/segments without racing the recorder.</summary>
    public SessionStore? ActiveSession => _active?.Session;
    public SegmentIndex? ActiveSegments => _active?.Segments;
    public int? ActiveGamePid => _active?.GamePid;
    public bool RecorderDied => _active is { Recorder.HasExited: true };

    // ---------------------------------------------------------------- start / stop

    public async Task<ControlResponse> StartAsync(ProfileConfig profileCfg, string trigger, int? gamePid, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_active is not null)
                return Fail($"already recording profile '{_active.Profile.Name}' (started by {_active.Trigger})");

            var profile = _cfg.Effective(profileCfg);
            if (profile.Mode == CaptureMode.Timeline && profile.TimelineStorage == "ram")
            {
                // RAM footage of earlier sessions goes when a new one starts, then check the window fits.
                PurgeRamSessions(all: true);
                var ramDir = Paths.Expand(_cfg.Timeline.RamDir);
                Directory.CreateDirectory(ramDir);
                var need = profile.WindowBytes(_cfg.Capture.BitrateKbps) * 11 / 10;   // + audio and slack
                long free = 0; try { free = new DriveInfo(ramDir).AvailableFreeSpace; } catch { }
                if (need > free)
                {
                    _sound.Play("error");
                    _events.Write("error", $"RAM timeline refused: {profile.WindowMinutes} min window needs {need / 1e9:0.0} GB, tmpfs has {free / 1e9:0.0} GB free", profile.Name, level: "error");
                    return Fail($"RAM timeline: a {profile.WindowMinutes} min window at {_cfg.Capture.BitrateKbps} kbps needs {need / 1e9:0.0} GB but {ramDir} has {free / 1e9:0.0} GB free. Shorten the window or lower the bitrate.");
                }
            }
            var session = SessionStore.Create(_cfg, profileCfg, trigger, gamePid);
            var sock = ControlPaths.SocketPath().Replace(".sock", "") + "-gsr.sock";   // next to the control socket, so test daemons stay isolated
            var incoming = Path.Combine(_cfg.ClipsDir, ".incoming");
            Directory.CreateDirectory(incoming);
            var args = GsrArguments.Build(_cfg, profile, session.MediaDir, incoming, sock);
            GsrProcess rec;
            try
            {
                rec = GsrProcess.Start(args, sock, _log);
                await rec.WaitForSocketAsync(TimeSpan.FromSeconds(15), ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "could not start gpu-screen-recorder");
                TryDeleteSession(session.Dir);
                _sound.Play("error");
                _events.Write("error", "could not start gpu-screen-recorder: " + ex.Message, profile.Name, level: "error");
                return Fail("could not start gpu-screen-recorder: " + ex.Message);
            }

            _active = new Active
            {
                Profile = profile, Session = session, Recorder = rec, Trigger = trigger, GamePid = gamePid,
                Segments = profile.Mode == CaptureMode.Timeline ? new SegmentIndex(session.Dir, session.MediaDir) : null,
            };
            var detail = profile.Mode == CaptureMode.Timeline
                ? (profile.WindowMinutes > 0 ? $"{profile.WindowMinutes} min window" : "unlimited window") + (profile.TimelineStorage == "ram" ? " in RAM" : "")
                : $"{profile.BufferMinutes} min {profile.BufferStorage} buffer";
            _log.LogInformation("session {id} started ({mode}, {detail}, trigger={trigger}, pid={pid})", session.Info.Id, profile.Mode, detail, trigger, gamePid);
            _events.Write("session", $"session started ({profile.Mode.ToString().ToLower()}, {detail}, {trigger})", profile.Name, session.Info.Id);
            _sound.Play("start");
            return Ok($"recording '{profile.Name}' ({profile.Mode.ToString().ToLower()}, {detail}) -> {session.Dir}");
        }
        finally { _lock.Release(); }
    }

    public async Task<ControlResponse> StopAsync(string reason, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_active is null) return Fail("not recording");
            var a = _active;
            _active = null;
            var result = await StopActiveAsync(a, reason, ct);
            _sound.Play("stop");
            _configService.ApplyPendingIfAny();
            return result;
        }
        finally { _lock.Release(); }
    }

    private async Task<ControlResponse> StopActiveAsync(Active a, string reason, CancellationToken ct)
    {
        _log.LogInformation("stopping session {id} ({reason})", a.Session.Info.Id, reason);
        try
        {
            if (!a.Recorder.HasExited)
            {
                if (a.Pending is not null) await FlushMarkJobAsync(a, "session ending", ct);
                await a.Recorder.StopAsync(ct);
                await a.Recorder.WaitForExitAsync(TimeSpan.FromSeconds(20), ct);
            }
        }
        catch (Exception ex) { _log.LogError(ex, "error while stopping recorder"); }
        finally { await a.Recorder.DisposeAsync(); }

        double? duration = null;
        if (a.Segments is not null)
        {
            a.Segments.Refresh();
            duration = a.Segments.TotalSeconds;
            a.Session.Info.RecordedBytes = a.Segments.TotalBytes;
        }
        else
        {
            duration = (MonotonicClock.NowMicros() - a.Session.Info.SpawnMonoUs) / 1e6;
            a.Session.Info.RecordedBytes = EstimateBufferBytes(a);
            a.Session.Info.BytesEstimated = true;
        }
        a.Session.Finish(duration);
        var toRam = a.Profile.Mode == CaptureMode.Buffer || a.Session.Info.MediaDir is not null;
        _stats.Update(c => { c.Sessions++; c.SecondsRecorded += duration ?? 0; if (toRam) c.RamBytes += a.Session.Info.RecordedBytes; else c.DiskBytes += a.Session.Info.RecordedBytes; c.Marks += a.Session.Markers.Count(m => m.Type == "manual"); });
        _events.Write("session", $"session ended ({reason}): {duration:0}s, {a.Session.Info.RecordedBytes / 1e6:0} MB to {(toRam ? "RAM" : "disk")}{(a.Session.Info.BytesEstimated ? " (estimated)" : "")}, {a.Session.Markers.Count} markers, {a.Session.Info.Clips.Count} clips",
            a.Profile.Name, a.Session.Info.Id, bytes: a.Session.Info.RecordedBytes, seconds: duration);

        var keep = a.Profile.Mode == CaptureMode.Timeline || a.Session.Markers.Count > 0;
        if (!keep) { TryDeleteSession(a.Session.Dir); return Ok($"session {a.Session.Info.Id} ended (nothing saved)"); }
        if (a.Profile.Mode == CaptureMode.Timeline && a.Session.Info.FirstFrameMonoUs is null)
            _log.LogWarning("session {id}: no first-frame timestamp found; markers keep monotonic times only", a.Session.Info.Id);
        var kept = a.Segments?.Available(includeTail: false).Sum(s => s.Duration);
        a.Session.RenameForMap();

        var msg = $"session {a.Session.Info.Id} saved" + (duration is double d ? $" ({d:0}s recorded" + (kept is double k && k < d - 1 ? $", last {k:0}s kept)" : ")") : "");
        _log.LogInformation("{msg}", msg);
        return Ok(msg);
    }

    // ---------------------------------------------------------------- mark / save

    public async Task<ControlResponse> MarkAsync(string? label, CancellationToken ct)
    {
        var tMono = MonotonicClock.NowMicros();   // capture before waiting on the lock
        await _lock.WaitAsync(ct);
        try
        {
            if (_active is null) { _sound.Play("error"); return Fail("not recording"); }
            var a = _active;
            a.Session.TryResolveFirstFrame();
            var m = a.Session.AddMarker("manual", label, tMono);
            _sound.Play("mark");

            var where = m.TVideo is double tv ? $"{tv:0.00}s" : "t=?";
            _events.Write("mark", $"mark at {where}" + (label is null ? "" : $" \"{label}\"") + (a.Profile.MarkSaves ? $", clip in {a.Profile.MarkAfterSeconds}s" : ""), a.Profile.Name, a.Session.Info.Id, seconds: m.TVideo);
            if (!a.Profile.MarkSaves)
            {
                _log.LogInformation("marker {id} '{label}' at {where}", m.Id, label ?? "", where);
                return Ok($"marked at {where}" + (label is null ? "" : $" \"{label}\""));
            }
            await ScheduleMarkClipAsync(a, m, tMono, ct);
            return Ok($"marked at {where}; clip in {a.Profile.MarkAfterSeconds}s" + (label is null ? "" : $" \"{label}\""));
        }
        finally { _lock.Release(); }
    }

    /// <summary>A game event from GSI: becomes a typed marker, and a clip if the profile's event_clips say so.</summary>
    public async Task EventMarkAsync(GsiEvent ev, long tMono, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var a = _active;
            if (a is null || a.Profile.Profile.Events != "cs2-gsi") return;
            a.Session.TryResolveFirstFrame();
            var m = a.Session.AddMarker(ev.Type, ev.Label, tMono, ev.Data);
            var where = m.TVideo is double tv ? $"{tv:0.00}s" : "t=?";
            var kills = ev.Data.TryGetValue("count", out var c) && c.TryGetInt32(out var k) ? k : 0;
            var clip = a.Profile.EventWantsClip(ev.Type, kills);
            _log.LogInformation("event {type} '{label}' at {where}{clip}", ev.Type, ev.Label ?? "", where, clip ? " (clip)" : "");
            _events.Write("mark", $"{ev.Type}{(ev.Label is null ? "" : " " + ev.Label)} at {where}" + (clip ? $", clip in {a.Profile.MarkAfterSeconds}s" : ""), a.Profile.Name, a.Session.Info.Id, seconds: m.TVideo);
            if (clip) await ScheduleMarkClipAsync(a, m, tMono, ct);   // automated: no sound, the log has it
        }
        finally { _lock.Release(); }
    }

    public void SetMap(string map)
    {
        var a = _active;
        if (a is null) return;
        a.Session.SetMap(map);
        _log.LogInformation("map {map}", map);
    }

    /// <summary>Queues a clip of [before, after] around a marker. Marks that arrive while one is pending join it.</summary>
    private async Task ScheduleMarkClipAsync(Active a, Marker m, long tMono, CancellationToken ct)
    {
        var p = a.Profile;
        var job = a.Pending;
        if (job is not null)
        {
            var span = (tMono - job.FirstMarkMonoUs) / 1_000_000.0 + p.MarkBeforeSeconds + p.MarkAfterSeconds;
            var capacity = p.Mode == CaptureMode.Buffer ? p.BufferMinutes * 60 : (p.WindowMinutes > 0 ? p.WindowMinutes * 60 : int.MaxValue);
            if (span > capacity) { await FlushMarkJobAsync(a, "buffer full", ct); job = null; }
        }
        if (job is null) { job = new MarkJob { FirstMarkMonoUs = tMono }; a.Pending = job; }
        job.LastMarkMonoUs = tMono;
        job.DueMonoUs = tMono + (long)p.MarkAfterSeconds * 1_000_000;
        job.Marks.Add(m);
        _log.LogInformation("marker {id} queued; clip will be written in {after}s ({n} marks in clip)", m.Id, p.MarkAfterSeconds, job.Marks.Count);
    }

    public async Task<ControlResponse> SaveAsync(int? seconds, string? label, CancellationToken ct)
    {
        var tMono = MonotonicClock.NowMicros();
        await _lock.WaitAsync(ct);
        try
        {
            if (_active is null) { _sound.Play("error"); return Fail("not recording"); }
            var a = _active;
            var secs = seconds ?? a.Profile.SaveSeconds;
            _sound.Play("save");
            return a.Profile.Mode == CaptureMode.Timeline
                ? await SaveFromTimelineAsync(a, secs, label, tMono, ct)
                : await SaveFromBufferAsync(a, secs, label, "save", null, ct);
        }
        finally { _lock.Release(); }
    }

    private async Task<ControlResponse> SaveFromTimelineAsync(Active a, int secs, string? label, long tMono, CancellationToken ct)
    {
        a.Session.TryResolveFirstFrame();
        var now = a.Session.ToVideoTime(tMono);
        if (now is null) { _sound.Play("error"); return Fail("timeline has no first frame yet"); }
        return await ExportFromTimelineAsync(a, Math.Max(0, now.Value - secs), now.Value, label, "save", null, tMono, ct);
    }

    /// <summary>Cuts [start, end] of the live timeline to a clip. Waits for the segment covering "end" to close first.</summary>
    private async Task<ControlResponse> ExportFromTimelineAsync(Active a, double start, double end, string? label, string source, MarkJob? job, long tMono, CancellationToken ct)
    {
        a.Segments!.Refresh();
        var secs = (int)Math.Round(end - start);
        // The recorder writes a segment to disk only when it completes, so wait for the one covering "now".
        var deadline = MonotonicClock.NowMicros() + (long)(a.Profile.SegmentSeconds + 5) * 1_000_000;
        while (a.Segments.TotalSeconds < end && MonotonicClock.NowMicros() < deadline && !a.Recorder.HasExited)
        {
            await Task.Delay(200, ct);
            a.Segments.Refresh();
        }
        var target = ClipNaming.NewPath(_cfg.ClipsDir, a.Profile.Name, label);
        var r = await ClipExporter.ExportAsync(a.Session.Dir, a.Segments, start, end, target, exact: false, _log, ct);
        if (!r.Ok) { if (source != "mark") _sound.Play("error"); return Fail($"{source} failed: " + r.Error); }

        var actualStart = end - (r.Duration ?? (end - start));   // stream copy may begin up to a keyframe early
        var info = new ClipInfo
        {
            Profile = a.Profile.Name, Label = label, Source = source, SessionId = a.Session.Info.Id,
            SessionOffset = actualStart, DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now,
            ExpiresAt = source == "mark" && a.Profile.MarkClipExpireHours > 0 ? DateTimeOffset.Now.AddHours(a.Profile.MarkClipExpireHours) : null,
        };
        if (job is not null)
        {
            foreach (var m in job.Marks)
            {
                var t = (m.TVideo ?? a.Session.ToVideoTime(m.TMonoUs) ?? actualStart) - actualStart;
                info.Markers.Add(new ClipMarker { T = Math.Max(0, t), Type = m.Type, Label = m.Label, Data = m.Data });
                a.Session.MarkerWrittenToClip(m, Path.GetFileName(target));
            }
        }
        else
        {
            var m = a.Session.AddMarker("save", label, tMono);
            info.Markers.Add(new ClipMarker { T = Math.Max(0, (r.Duration ?? secs)), Type = "save", Label = label });
            a.Session.MarkerWrittenToClip(m, Path.GetFileName(target));
        }
        ClipNaming.WriteSidecar(target, info);
        a.Session.Info.Clips.Add(Path.GetFileName(target));
        a.Session.Save();
        _log.LogInformation("{source}: wrote {dur:0.0}s ({start:0.0}-{end:0.0}s) -> {path}", source, r.Duration ?? 0, actualStart, end, target);
        RecordClip(source, target, r.Duration, a);
        return Ok(source == "save" ? $"saved last {secs}s -> {target}" : $"mark clip written -> {target}");
    }

    private async Task<ControlResponse> SaveFromBufferAsync(Active a, int secs, string? label, string source, MarkJob? job, CancellationToken ct)
    {
        secs = Math.Min(secs, a.Profile.BufferMinutes * 60);
        var saveMono = MonotonicClock.NowMicros();
        var reply = await a.Recorder.SaveReplayAsync(secs, ct);
        if (!reply.Ok || reply.Data is null) { if (source != "mark") _sound.Play("error"); return Fail("save failed: " + reply.Data); }

        var target = ClipNaming.Place(_cfg.ClipsDir, reply.Data, a.Profile.Name, label);
        var duration = await VideoProbe.DurationSecondsAsync(target, ct) ?? secs;
        var info = new ClipInfo
        {
            Profile = a.Profile.Name, Label = label, Source = source, SessionId = a.Session.Info.Id,
            SessionOffset = a.Session.ToVideoTime(saveMono) is double tEnd ? Math.Max(0, tEnd - duration) : null,
            DurationSeconds = duration, CreatedAt = DateTimeOffset.Now,
            ExpiresAt = source == "mark" && a.Profile.MarkClipExpireHours > 0 ? DateTimeOffset.Now.AddHours(a.Profile.MarkClipExpireHours) : null,
        };
        if (job is not null)
        {
            // The clip ends at the save moment; each mark sits (save - mark) seconds before the end.
            foreach (var m in job.Marks)
            {
                var t = duration - (saveMono - m.TMonoUs) / 1_000_000.0;
                info.Markers.Add(new ClipMarker { T = Math.Max(0, t), Type = m.Type, Label = m.Label, Data = m.Data });
                a.Session.MarkerWrittenToClip(m, Path.GetFileName(target));
            }
        }
        else info.Markers.Add(new ClipMarker { T = duration, Type = "save", Label = label });
        ClipNaming.WriteSidecar(target, info);
        a.Session.Info.Clips.Add(Path.GetFileName(target));
        a.Session.Save();
        _log.LogInformation("{source}: wrote {dur:0.0}s -> {path}", source, duration, target);
        RecordClip(source, target, duration, a);
        return Ok($"saved {duration:0}s -> {target}");
    }

    private async Task FlushMarkJobAsync(Active a, string why, CancellationToken ct)
    {
        var job = a.Pending;
        if (job is null) return;
        a.Pending = null;
        var p = a.Profile;
        var now = MonotonicClock.NowMicros();
        var secs = (int)Math.Ceiling((now - job.FirstMarkMonoUs) / 1_000_000.0) + p.MarkBeforeSeconds;
        var label = job.Marks.Select(m => m.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        _log.LogInformation("writing mark clip ({why}): {n} marks, last {secs}s", why, job.Marks.Count, secs);
        ControlResponse r;
        if (p.Mode == CaptureMode.Buffer) r = await SaveFromBufferAsync(a, secs, label, "mark", job, ct);
        else
        {
            a.Session.TryResolveFirstFrame();
            var end = a.Session.ToVideoTime(now) ?? 0;
            var first = a.Session.ToVideoTime(job.FirstMarkMonoUs) ?? end;
            r = await ExportFromTimelineAsync(a, Math.Max(0, first - p.MarkBeforeSeconds), end, label, "mark", job, now, ct);
        }
        // Clip written in the background: silent. You already heard the mark itself if you pressed it.
    }

    private long _lastPurgeMono;
    private readonly ProcessUsage _usage = new();
    private ProcessUsage.Sample _lastUsage = new(0, 0, 0);
    private long _lastUsageAt;

    /// <summary>CPU and RSS of the daemon plus the recorder and its helper. Sampled at most every 2 s.</summary>
    public ProcessUsage.Sample Usage()
    {
        var now = Environment.TickCount64;
        if (now - _lastUsageAt < 2000) return _lastUsage;
        _lastUsageAt = now;
        var pids = new List<int> { Environment.ProcessId };
        if (_active is { Recorder.HasExited: false } a) { pids.Add(a.Recorder.Pid); pids.AddRange(ProcessUsage.Children(a.Recorder.Pid)); }
        return _lastUsage = _usage.Measure(pids);
    }

    /// <summary>Frees the tmpfs: drops RAM footage of every finished session and any stray directories. Never touches the live session.</summary>
    public (int Sessions, long Bytes) ClearRam()
    {
        var ramDir = Paths.Expand(_cfg.Timeline.RamDir);
        var before = ProcessUsage.DirBytes(ramDir);
        var n = 0;
        PurgeRamSessions(all: true);
        if (Directory.Exists(ramDir))
        {
            foreach (var d in Directory.EnumerateDirectories(ramDir))
            {
                if (d == _active?.Session.MediaDir) continue;
                try { Directory.Delete(d, true); n++; } catch { }
            }
        }
        var freed = before - ProcessUsage.DirBytes(ramDir);
        _events.Write("purge", $"RAM cleared by request: {freed / 1e9:0.00} GB freed", bytes: freed);
        return (n, freed);
    }

    /// <summary>Drops RAM footage of finished sessions: all of them, or only those past their keep time.</summary>
    public void PurgeRamSessions(bool all)
    {
        if (!Directory.Exists(_cfg.SessionsDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(_cfg.SessionsDir))
        {
            if (dir == _active?.Session.Dir) continue;
            SessionStore store;
            try { if (!File.Exists(Path.Combine(dir, "session.json"))) continue; store = SessionStore.Load(dir); } catch { continue; }
            if (store.Info.MediaDir is null || store.Info.FootageGone) continue;
            var keep = _cfg.FindProfile(store.Info.Profile) is { } p ? _cfg.Effective(p).RamKeepHours : _cfg.Timeline.RamKeepHours;
            var ended = store.Info.EndedAt ?? store.Info.StartedAt;
            var expired = (DateTimeOffset.Now - ended).TotalHours >= keep;
            var missing = !Directory.Exists(store.Info.MediaDir);
            if (all || expired || missing)
            {
                var why = missing ? "already gone" : all ? "new session" : "keep time passed";
                _log.LogInformation("dropping RAM footage of {id} ({why})", store.Info.Id, why);
                _events.Write("purge", $"RAM footage dropped ({why})", store.Info.Profile, store.Info.Id, bytes: store.Info.RecordedBytes);
                store.DropMedia();
            }
        }
    }

    /// <summary>Called by the watcher loop about once a second. Cheap: an mtime check and a due-time compare.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var nowMono = MonotonicClock.NowMicros();
        if (nowMono - _lastPurgeMono > 60_000_000) { _lastPurgeMono = nowMono; try { PurgeRamSessions(all: false); } catch (Exception ex) { _log.LogDebug(ex, "purge"); } }
        if (_active is null) return;
        if (!await _lock.WaitAsync(0, ct)) return;
        try
        {
            var a = _active;
            if (a is null || a.Recorder.HasExited) return;
            a.Segments?.Refresh();
            if (a.Pending is { } job && MonotonicClock.NowMicros() >= job.DueMonoUs)
                await FlushMarkJobAsync(a, "due", ct);
        }
        finally { _lock.Release(); }
    }

    // ---------------------------------------------------------------- status / misc

    public DaemonStatus Status()
    {
        var a = _active;
        var s = new DaemonStatus { Profiles = _cfg.Profiles.Select(p => p.Name).ToList(), DaemonPid = Environment.ProcessId, RootDir = _cfg.RootDir,
            UiUrl = $"http://{_cfg.Daemon.Bind}:{_cfg.Daemon.UiPort}/", SaveSeconds = _cfg.Clips.SaveSeconds };
        if (a is null) return s;
        s.RecorderPid = a.Recorder.HasExited ? null : a.Recorder.Pid;
        s.SaveSeconds = a.Profile.SaveSeconds;
        s.SessionStartedAt = a.Session.Info.StartedAt;
        s.State = a.Recorder.HasExited ? "recorder-died" : "recording";
        s.Profile = a.Profile.Name;
        s.Mode = a.Profile.Mode.ToString().ToLowerInvariant();
        s.SessionDir = a.Session.Dir;
        s.GamePid = a.GamePid;
        s.MarkerCount = a.Session.Markers.Count;
        s.ElapsedSeconds = (MonotonicClock.NowMicros() - a.Session.Info.SpawnMonoUs) / 1_000_000.0;
        if (a.Segments is not null)
        {
            s.RecordedSeconds = a.Segments.TotalSeconds;
            s.WindowSeconds = a.Segments.Available(includeTail: false).Sum(x => x.Duration);
        }
        s.PendingMarks = a.Pending?.Marks.Count ?? 0;
        return s;
    }

    private void RecordClip(string source, string path, double? duration, Active a)
    {
        long bytes = 0; try { bytes = new FileInfo(path).Length; } catch { }
        _stats.Update(c => { c.Clips++; c.ClipBytes += bytes; if (source == "save") c.Saves++; });
        _events.Write("clip", $"{(source == "mark" ? "mark clip" : source == "save" ? "save" : "export")} written: {duration ?? 0:0.0}s, {bytes / 1e6:0.0} MB", a.Profile.Name, a.Session.Info.Id, Path.GetFileName(path), bytes, duration);
    }

    /// <summary>For exports made through the web API (which do not go through the coordinator).</summary>
    public void RecordExport(string profile, string? session, string path, double? duration)
    {
        long bytes = 0; try { bytes = new FileInfo(path).Length; } catch { }
        _stats.Update(c => { c.Clips++; c.ClipBytes += bytes; c.Exports++; });
        _events.Write("export", $"export written: {duration ?? 0:0.0}s, {bytes / 1e6:0.0} MB", profile, session, Path.GetFileName(path), bytes, duration);
    }

    private static ControlResponse Ok(string msg) => new() { Ok = true, Message = msg };
    private static ControlResponse Fail(string msg) => new() { Ok = false, Message = msg };

    private void TryDeleteSession(string dir)
    {
        try
        {
            var junk = new[] { "session.json", "markers.json", "segments.json", "live.m3u8", "live.m3u8.ts" };
            if (Directory.EnumerateFiles(dir).All(f => junk.Contains(Path.GetFileName(f))))
            {
                try { var info = SessionStore.Load(dir).Info; if (info.MediaDir is not null && Directory.Exists(info.MediaDir)) Directory.Delete(info.MediaDir, true); } catch { }
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "could not remove {dir}", dir); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_active is not null) await StopAsync("daemon shutdown", CancellationToken.None);
    }
}
