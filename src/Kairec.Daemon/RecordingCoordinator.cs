using Kairec.Core;
using Kairec.Core.Config;
using Kairec.Core.Export;
using Kairec.Core.Feedback;
using Kairec.Core.Recording;
using Kairec.Core.Sessions;
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

    public RecordingCoordinator(KairecConfig cfg, SoundFeedback sound, ILogger<RecordingCoordinator> log)
    {
        _cfg = cfg; _sound = sound; _log = log;
    }

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
            var session = SessionStore.Create(_cfg, profileCfg, trigger, gamePid);
            var sock = Path.Combine(Path.GetDirectoryName(ControlPaths.SocketPath())!, "kairec-gsr.sock");
            var incoming = Path.Combine(_cfg.ClipsDir, ".incoming");
            Directory.CreateDirectory(incoming);
            var args = GsrArguments.Build(_cfg, profile, session.Dir, incoming, sock);
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
                return Fail("could not start gpu-screen-recorder: " + ex.Message);
            }

            _active = new Active
            {
                Profile = profile, Session = session, Recorder = rec, Trigger = trigger, GamePid = gamePid,
                Segments = profile.Mode == CaptureMode.Timeline ? new SegmentIndex(session.Dir) : null,
            };
            var detail = profile.Mode == CaptureMode.Timeline
                ? (profile.WindowMinutes > 0 ? $"{profile.WindowMinutes} min window" : "unlimited window")
                : $"{profile.BufferMinutes} min {profile.BufferStorage} buffer";
            _log.LogInformation("session {id} started ({mode}, {detail}, trigger={trigger}, pid={pid})", session.Info.Id, profile.Mode, detail, trigger, gamePid);
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
        }
        a.Session.Finish(duration);

        var keep = a.Profile.Mode == CaptureMode.Timeline || a.Session.Markers.Count > 0;
        if (!keep) { TryDeleteSession(a.Session.Dir); return Ok($"session {a.Session.Info.Id} ended (nothing saved)"); }
        if (a.Profile.Mode == CaptureMode.Timeline && a.Session.Info.FirstFrameMonoUs is null)
            _log.LogWarning("session {id}: no first-frame timestamp found; markers keep monotonic times only", a.Session.Info.Id);
        a.Session.RenameForMap();

        var kept = a.Segments?.Available(includeTail: false).Sum(s => s.Duration);
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

            if (a.Profile.Mode == CaptureMode.Timeline)
            {
                var where = m.TVideo is double t ? $"{t:0.00}s" : "t=?";
                _log.LogInformation("marker {id} '{label}' at {where}", m.Id, label ?? "", where);
                return Ok($"marked at {where}" + (label is null ? "" : $" \"{label}\""));
            }

            // Buffer mode: schedule a dump of [before, after] around the mark. Marks that arrive while
            // one is pending join it, unless the buffer could not hold the combined span.
            var p = a.Profile;
            var job = a.Pending;
            if (job is not null)
            {
                var span = (tMono - job.FirstMarkMonoUs) / 1_000_000.0 + p.MarkBeforeSeconds + p.MarkAfterSeconds;
                if (span > p.BufferMinutes * 60) { await FlushMarkJobAsync(a, "buffer full", ct); job = null; }
            }
            if (job is null) { job = new MarkJob { FirstMarkMonoUs = tMono }; a.Pending = job; }
            job.LastMarkMonoUs = tMono;
            job.DueMonoUs = tMono + (long)p.MarkAfterSeconds * 1_000_000;
            job.Marks.Add(m);
            _log.LogInformation("marker {id} '{label}' queued; clip will be written in {after}s ({n} marks in clip)", m.Id, label ?? "", p.MarkAfterSeconds, job.Marks.Count);
            return Ok($"marked; clip in {p.MarkAfterSeconds}s" + (label is null ? "" : $" \"{label}\""));
        }
        finally { _lock.Release(); }
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
        a.Segments!.Refresh();
        var now = a.Session.ToVideoTime(tMono);
        if (now is null) { _sound.Play("error"); return Fail("timeline has no first frame yet"); }
        var end = now.Value;
        var start = Math.Max(0, end - secs);
        // The recorder writes a segment to disk only when it completes, so wait for the one covering "now".
        var deadline = MonotonicClock.NowMicros() + (long)(a.Profile.SegmentSeconds + 5) * 1_000_000;
        while (a.Segments.TotalSeconds < end && MonotonicClock.NowMicros() < deadline && !a.Recorder.HasExited)
        {
            await Task.Delay(200, ct);
            a.Segments.Refresh();
        }
        var target = ClipNaming.NewPath(_cfg.ClipsDir, a.Profile.Name, label);
        var r = await ClipExporter.ExportAsync(a.Session.Dir, a.Segments, start, end, target, exact: false, _log, ct);
        if (!r.Ok) { _sound.Play("error"); return Fail("save failed: " + r.Error); }

        var m = a.Session.AddMarker("save", label, tMono);
        var info = new ClipInfo
        {
            Profile = a.Profile.Name, Label = label, Source = "save", SessionId = a.Session.Info.Id,
            SessionOffset = start, DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now,
            Markers = [new ClipMarker { T = Math.Max(0, (r.Duration ?? secs) - 0.0), Type = "save", Label = label }],
        };
        ClipNaming.WriteSidecar(target, info);
        a.Session.MarkerWrittenToClip(m, Path.GetFileName(target));
        a.Session.Info.Clips.Add(Path.GetFileName(target));
        a.Session.Save();
        _log.LogInformation("saved last {secs}s ({dur:0.0}s) -> {path}", secs, r.Duration ?? 0, target);
        return Ok($"saved last {secs}s -> {target}");
    }

    private async Task<ControlResponse> SaveFromBufferAsync(Active a, int secs, string? label, string source, MarkJob? job, CancellationToken ct)
    {
        secs = Math.Min(secs, a.Profile.BufferMinutes * 60);
        var saveMono = MonotonicClock.NowMicros();
        var reply = await a.Recorder.SaveReplayAsync(secs, ct);
        if (!reply.Ok || reply.Data is null) { _sound.Play("error"); return Fail("save failed: " + reply.Data); }

        var target = ClipNaming.Place(_cfg.ClipsDir, reply.Data, a.Profile.Name, label);
        var duration = await VideoProbe.DurationSecondsAsync(target, ct) ?? secs;
        var info = new ClipInfo
        {
            Profile = a.Profile.Name, Label = label, Source = source, SessionId = a.Session.Info.Id,
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
        var r = await SaveFromBufferAsync(a, secs, label, "mark", job, ct);
        if (r.Ok) _sound.Play("save");
    }

    /// <summary>Called by the watcher loop about once a second. Cheap: an mtime check and a due-time compare.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
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
        var s = new DaemonStatus { Profiles = _cfg.Profiles.Select(p => p.Name).ToList() };
        if (a is null) return s;
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

    private static ControlResponse Ok(string msg) => new() { Ok = true, Message = msg };
    private static ControlResponse Fail(string msg) => new() { Ok = false, Message = msg };

    private void TryDeleteSession(string dir)
    {
        try
        {
            var junk = new[] { "session.json", "markers.json", "segments.json", "live.m3u8", "live.m3u8.ts" };
            if (Directory.EnumerateFiles(dir).All(f => junk.Contains(Path.GetFileName(f))))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { _log.LogDebug(ex, "could not remove {dir}", dir); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_active is not null) await StopAsync("daemon shutdown", CancellationToken.None);
    }
}
