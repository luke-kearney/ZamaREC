using System.Text.Json;
using ZamaRec.Core.Config;

namespace ZamaRec.Core.Sessions;

public sealed record SessionSummary(string Id, string Dir, SessionInfo Info, int MarkerCount);
/// <param name="At">When the clip's footage was recorded: what lists show and sort by.</param>
public sealed record ClipSummary(string Profile, string File, string Path, long Bytes, ClipInfo Info, DateTimeOffset At);

/// <summary>Read-side view over sessions/ and clips/ for the UI and CLI.</summary>
public sealed class SessionCatalog
{
    private readonly ZamaRecConfig _cfg;
    public SessionCatalog(ZamaRecConfig cfg) => _cfg = cfg;

    public List<SessionSummary> Sessions()
    {
        var list = new List<SessionSummary>();
        if (!Directory.Exists(_cfg.SessionsDir)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_cfg.SessionsDir))
        {
            var sp = Path.Combine(dir, "session.json");
            if (!File.Exists(sp)) continue;
            try
            {
                var info = JsonSerializer.Deserialize(File.ReadAllText(sp), SessionJson.Default.SessionInfo);
                if (info is null) continue;
                var mp = Path.Combine(dir, "markers.json");
                var count = File.Exists(mp) ? (JsonSerializer.Deserialize(File.ReadAllText(mp), SessionJson.Default.MarkerFile)?.Markers.Count ?? 0) : 0;
                list.Add(new SessionSummary(Path.GetFileName(dir), dir, info, count));
            }
            catch (Exception) { /* half-written or foreign folder */ }
        }
        return list.OrderByDescending(s => s.Info.StartedAt).ToList();
    }

    public string? SessionDir(string id)
    {
        if (id.Contains('/') || id.Contains("..")) return null;
        var dir = Path.Combine(_cfg.SessionsDir, id);
        return Directory.Exists(dir) ? dir : null;
    }

    public List<ClipSummary> Clips()
    {
        var list = new List<ClipSummary>();
        if (!Directory.Exists(_cfg.ClipsDir)) return list;
        var sessions = new Dictionary<string, SessionInfo?>();
        foreach (var pdir in Directory.EnumerateDirectories(_cfg.ClipsDir))
        {
            var profile = Path.GetFileName(pdir);
            if (profile.StartsWith('.')) continue;
            foreach (var mp4 in Directory.EnumerateFiles(pdir, "*.mp4"))
            {
                ClipInfo info;
                try
                {
                    var side = mp4 + ".json";
                    info = File.Exists(side) ? JsonSerializer.Deserialize(File.ReadAllText(side), SessionJson.Default.ClipInfo) ?? new ClipInfo { Profile = profile } : new ClipInfo { Profile = profile, CreatedAt = File.GetCreationTime(mp4) };
                }
                catch { info = new ClipInfo { Profile = profile }; }
                list.Add(new ClipSummary(profile, Path.GetFileName(mp4), mp4, new FileInfo(mp4).Length, info, FootageTime(info, sessions)));
            }
        }
        return list.OrderByDescending(c => c.At).ToList();
    }

    /// <summary>
    /// When a clip's footage ends on the wall clock. Mark and save clips are written as they happen, so that is
    /// created_at. An export carries recorded_at; one from before that field existed is placed through its session.
    /// </summary>
    public DateTimeOffset FootageTime(ClipInfo info, Dictionary<string, SessionInfo?>? cache = null)
    {
        if (info.RecordedAt is { } at) return at;
        if (info.Source != "export" || info.SessionId is not { } sid || info.SessionOffset is not double offset) return info.CreatedAt;
        cache ??= new();
        if (!cache.TryGetValue(sid, out var session))
        {
            session = null;
            try
            {
                if (SessionDir(sid) is { } dir && File.Exists(Path.Combine(dir, "session.json")))
                    session = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, "session.json")), SessionJson.Default.SessionInfo);
            }
            catch { }
            cache[sid] = session;
        }
        return session?.TimeAt(offset + (info.DurationSeconds ?? 0)) ?? info.CreatedAt;
    }

    public string? ClipPath(string profile, string file)
    {
        if (profile.Contains('/') || profile.Contains("..") || file.Contains('/') || file.Contains("..")) return null;
        var p = Path.Combine(_cfg.ClipsDir, profile, file);
        return File.Exists(p) ? p : null;
    }

    public static void SaveClipInfo(string clipPath, ClipInfo info) => ClipNaming.WriteSidecar(clipPath, info);
}
