using System.Text.Json;
using Kairec.Core.Config;

namespace Kairec.Core.Sessions;

public sealed record SessionSummary(string Id, string Dir, SessionInfo Info, int MarkerCount);
public sealed record ClipSummary(string Profile, string File, string Path, long Bytes, ClipInfo Info);

/// <summary>Read-side view over sessions/ and clips/ for the UI and CLI.</summary>
public sealed class SessionCatalog
{
    private readonly KairecConfig _cfg;
    public SessionCatalog(KairecConfig cfg) => _cfg = cfg;

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
                list.Add(new ClipSummary(profile, Path.GetFileName(mp4), mp4, new FileInfo(mp4).Length, info));
            }
        }
        return list.OrderByDescending(c => c.Info.CreatedAt).ToList();
    }

    public string? ClipPath(string profile, string file)
    {
        if (profile.Contains('/') || profile.Contains("..") || file.Contains('/') || file.Contains("..")) return null;
        var p = Path.Combine(_cfg.ClipsDir, profile, file);
        return File.Exists(p) ? p : null;
    }

    public static void SaveClipInfo(string clipPath, ClipInfo info) => ClipNaming.WriteSidecar(clipPath, info);
}
