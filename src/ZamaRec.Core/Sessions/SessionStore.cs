using System.Text.Json;
using ZamaRec.Core.Config;

namespace ZamaRec.Core.Sessions;

/// <summary>Owns one session directory: session.json, markers.json and the video the recorder writes into it.</summary>
public sealed class SessionStore
{
    private readonly object _lock = new();
    private readonly MarkerFile _markers;

    public string Dir { get; private set; }
    /// <summary>Directory holding the playlist and segments: the session dir, or tmpfs for RAM timelines.</summary>
    public string MediaDir => Info.MediaDir ?? Dir;
    public SessionInfo Info { get; }
    public IReadOnlyList<Marker> Markers => _markers.Markers;

    private SessionStore(string dir, SessionInfo info, MarkerFile markers)
    {
        Dir = dir; Info = info; _markers = markers;
    }

    public static SessionStore Create(ZamaRecConfig cfg, ProfileConfig profile, string trigger, int? gamePid, long? steamAppId = null)
    {
        var now = DateTimeOffset.Now;
        var id = $"{now:yyyy-MM-dd_HH-mm-ss}_{profile.Name}";
        var dir = Path.Combine(cfg.SessionsDir, id);
        Directory.CreateDirectory(dir);
        var eff = cfg.Effective(profile);
        string? mediaDir = null;
        if (eff.Mode == CaptureMode.Timeline && eff.TimelineStorage == "ram")
        {
            mediaDir = Path.Combine(Paths.Expand(cfg.Timeline.RamDir), id);
            Directory.CreateDirectory(mediaDir);
        }
        var info = new SessionInfo
        {
            Id = id, Profile = profile.Name, Mode = eff.Mode.ToString().ToLowerInvariant(), Trigger = trigger,
            StartedAt = now, SpawnMonoUs = MonotonicClock.NowMicros(), GamePid = gamePid, SteamAppId = steamAppId,
            Playlist = eff.Mode == CaptureMode.Timeline ? Recording.GsrArguments.PlaylistName : null,
            MediaDir = mediaDir,
            AudioTracks = cfg.Capture.EffectiveTracks() is { Count: > 0 } tracks ? tracks.Select(t => t.Name).ToList() : null,
        };
        // Buffer sessions never get a first-frame stamp (nothing is written until a save), so their
        // timeline origin is the recorder spawn. Clip offsets are computed against the same origin.
        if (eff.Mode == CaptureMode.Buffer) info.FirstFrameMonoUs = info.SpawnMonoUs;
        var store = new SessionStore(dir, info, new MarkerFile());
        store.Save();
        return store;
    }

    public static SessionStore Load(string dir)
    {
        var info = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, "session.json")), SessionJson.Default.SessionInfo)
                   ?? throw new InvalidDataException($"bad session.json in {dir}");
        var mpath = Path.Combine(dir, "markers.json");
        var markers = File.Exists(mpath)
            ? JsonSerializer.Deserialize(File.ReadAllText(mpath), SessionJson.Default.MarkerFile) ?? new MarkerFile()
            : new MarkerFile();
        return new SessionStore(dir, info, markers);
    }

    public Marker AddMarker(string type, string? label, long? tMonoUs = null, Dictionary<string, JsonElement>? data = null)
    {
        var m = new Marker
        {
            Type = type, Label = label, TMonoUs = tMonoUs ?? MonotonicClock.NowMicros(),
            At = DateTimeOffset.Now, Data = data,
        };
        lock (_lock)
        {
            m.TVideo = ToVideoTime(m.TMonoUs);
            _markers.Markers.Add(m);
            SaveMarkersLocked();
        }
        return m;
    }

    /// <summary>Seconds into the video for a monotonic timestamp, or null if the first frame time is unknown yet.</summary>
    public double? ToVideoTime(long tMonoUs) =>
        Info.FirstFrameMonoUs is long ff ? Math.Max(0, (tMonoUs - ff) / 1_000_000.0) : null;

    /// <summary>Reads the recorder's "&lt;video&gt;.ts" file if it exists and fills in first-frame times. Returns true once known.</summary>
    public bool TryResolveFirstFrame()
    {
        lock (_lock)
        {
            if (Info.FirstFrameMonoUs is not null) return true;
            var ts = FindTimestampFile();
            if (ts is null) return false;
            var parsed = FirstFrameTimestamp.TryRead(ts);
            if (parsed is null) return false;
            Info.FirstFrameMonoUs = parsed.Value.MonoUs;
            Info.FirstFrameRealtimeUs = parsed.Value.RealtimeUs;
            foreach (var m in _markers.Markers) m.TVideo ??= ToVideoTime(m.TMonoUs);
            SaveLocked();
            return true;
        }
    }

    private string? FindTimestampFile()
    {
        // gpu-screen-recorder writes "<output>.ts" next to its output: live.m3u8.ts in timeline mode.
        var p = Path.Combine(MediaDir, (Info.Playlist ?? "live.m3u8") + ".ts");
        return File.Exists(p) ? p : null;
    }

    public void Finish(double? durationSeconds)
    {
        lock (_lock)
        {
            Info.EndedAt = DateTimeOffset.Now;
            Info.DurationSeconds = durationSeconds;
        }
        TryResolveFirstFrame();
        Save();
    }

    public void MarkerWrittenToClip(Marker m, string clipFile)
    {
        lock (_lock) { m.Clip = clipFile; SaveMarkersLocked(); }
    }

    public void SetMap(string map)
    {
        lock (_lock) { Info.Map = map; SaveLocked(); }
    }

    /// <summary>Appends the map name to the directory once recording has stopped and the recorder has released the files.</summary>
    /// <summary>Drops a RAM timeline's segments and records that the footage is gone.</summary>
    public void DropMedia()
    {
        if (Info.MediaDir is null) return;
        try { if (Directory.Exists(Info.MediaDir)) Directory.Delete(Info.MediaDir, recursive: true); } catch (IOException) { }
        lock (_lock) { Info.FootageGone = true; SaveLocked(); }
    }

    public void RenameForMap()
    {
        if (string.IsNullOrWhiteSpace(Info.Map)) return;
        var safe = new string(Info.Map.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (safe.Length == 0 || Dir.EndsWith("_" + safe)) return;
        var target = Dir + "_" + safe;
        if (Directory.Exists(target)) return;
        Directory.Move(Dir, target);
        Dir = target;
        Info.Id = Path.GetFileName(target);
        Save();
    }

    public void Save() { lock (_lock) SaveLocked(); }

    private void SaveLocked()
    {
        WriteAtomic(Path.Combine(Dir, "session.json"), JsonSerializer.Serialize(Info, SessionJson.Default.SessionInfo));
        SaveMarkersLocked();
    }

    private void SaveMarkersLocked() =>
        WriteAtomic(Path.Combine(Dir, "markers.json"), JsonSerializer.Serialize(_markers, SessionJson.Default.MarkerFile));

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

public static class FirstFrameTimestamp
{
    /// <summary>Parses gpu-screen-recorder's .ts file: a header line then "monotonic_us realtime_us".</summary>
    public static (long MonoUs, long RealtimeUs)? TryRead(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[0], out var mono) && long.TryParse(parts[1], out var real))
                    return (mono, real);
            }
        }
        catch (IOException) { }
        return null;
    }
}
