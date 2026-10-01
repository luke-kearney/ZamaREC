using System.Text.Json;
using ZamaRec.Core.Config;
using ZamaRec.Core.Sessions;
using ZamaRec.Core.Telemetry;

namespace ZamaRec.Daemon;

/// <summary>
/// Keeps the recordings folder within bounds. Runs a minute after start and every ten minutes:
///   0. empty sessions go: finished, footage gone (or never written), no markers, no clips;
///   1. mark clips past their expires_at go, unless starred;
///   2. clips and finished sessions older than retention.max_days go, unless protected;
///   3. while a profile's sessions + clips exceed retention.max_gb, the oldest unprotected item goes.
/// Protected: starred clips, the live session, sessions with starred markers or with clips still on disk.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly ZamaRecConfig _cfg;
    private readonly RecordingCoordinator _rec;
    private readonly SessionCatalog _cat;
    private readonly EventLog _events;
    private readonly ILogger<RetentionService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RetentionService(ZamaRecConfig cfg, RecordingCoordinator rec, SessionCatalog cat, EventLog events, ILogger<RetentionService> log)
    {
        _cfg = cfg; _rec = rec; _cat = cat; _events = events; _log = log;
    }

    public sealed record Result(int ClipsDeleted, int SessionsDeleted, long BytesFreed, List<string> Details);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { RunNow(); } catch (Exception ex) { _log.LogError(ex, "retention pass failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { }
        }
    }

    private sealed record Item(string Kind, string Profile, string Path, DateTimeOffset Date, long Bytes, bool Protected, string Why, bool Empty = false);

    /// <summary>A finished session with nothing left to show: no footage, no markers, no clips.</summary>
    private static bool IsEmpty(SessionStore store, bool hasClips)
    {
        if (hasClips || store.Markers.Count > 0) return false;
        if (store.Info.FootageGone || store.Info.Mode == "buffer") return true;   // buffer sessions never keep footage of their own
        var media = store.MediaDir ?? store.Dir;
        return !Directory.Exists(media) || !Directory.EnumerateFiles(media, "*.ts").Any();
    }

    public Result RunNow()
    {
        if (!_lock.Wait(0)) return new Result(0, 0, 0, ["already running"]);
        try
        {
            var now = DateTimeOffset.Now;
            var details = new List<string>();
            int clips = 0, sessions = 0; long freed = 0;

            // ---- inventory
            var items = new List<Item>();
            var clipsBySession = new Dictionary<string, int>();
            foreach (var c in _cat.Clips())
            {
                var prot = c.Info.Starred;
                items.Add(new Item("clip", c.Profile, c.Path, c.Info.CreatedAt == default ? File.GetLastWriteTime(c.Path) : c.Info.CreatedAt, c.Bytes, prot, ""));
                if (c.Info.SessionId is string sid) clipsBySession[sid] = clipsBySession.GetValueOrDefault(sid) + 1;
            }
            foreach (var s in _cat.Sessions())
            {
                if (s.Id == _rec.ActiveSessionId) continue;
                var store = SessionStore.Load(s.Dir);
                var bytes = DirBytes(s.Dir);   // RAM media is not on disk and is purged by its own timer
                var starred = store.Markers.Any(m => m.Starred);
                var hasClips = clipsBySession.ContainsKey(s.Id);
                var prot = starred || hasClips;
                items.Add(new Item("session", s.Info.Profile, s.Dir, s.Info.EndedAt ?? s.Info.StartedAt, bytes, prot, starred ? "starred markers" : hasClips ? "has clips" : "", Empty: IsEmpty(store, hasClips)));
            }

            void Delete(Item it, string reason)
            {
                try
                {
                    if (it.Kind == "clip") { File.Delete(it.Path); if (File.Exists(it.Path + ".json")) File.Delete(it.Path + ".json"); clips++; }
                    else
                    {
                        try { var info = SessionStore.Load(it.Path).Info; if (info.MediaDir is not null && Directory.Exists(info.MediaDir)) Directory.Delete(info.MediaDir, true); } catch { }
                        Directory.Delete(it.Path, true); sessions++;
                    }
                    freed += it.Bytes;
                    var line = $"{it.Kind} {System.IO.Path.GetFileName(it.Path)} ({it.Bytes / 1e6:0} MB, {reason})";
                    details.Add(line);
                    _events.Write("purge", "retention removed " + line, it.Profile, bytes: it.Bytes);
                }
                catch (Exception ex) { _log.LogWarning(ex, "could not remove {path}", it.Path); }
            }

            // ---- 0. empty sessions: nothing to watch, nothing marked, nothing saved
            foreach (var it in items.Where(i => i.Kind == "session" && i.Empty && !i.Protected).ToList())
            { Delete(it, "empty: no footage, markers or clips"); items.Remove(it); }

            // ---- 1. expired mark clips
            foreach (var c in _cat.Clips())
            {
                if (c.Info.Starred || c.Info.ExpiresAt is not { } exp || exp > now) continue;
                var it = items.First(i => i.Path == c.Path);
                Delete(it, $"expired {exp:MM-dd HH:mm}"); items.Remove(it);
            }

            // ---- 2 & 3 per profile
            var profiles = items.Select(i => i.Profile).Distinct().ToList();
            foreach (var name in profiles)
            {
                var p = _cfg.FindProfile(name);
                var (maxGb, maxDays) = p is null ? (_cfg.Retention.MaxGb, _cfg.Retention.MaxDays) : (_cfg.Effective(p).MaxGb, _cfg.Effective(p).MaxDays);
                var mine = items.Where(i => i.Profile == name).OrderBy(i => i.Date).ToList();

                if (maxDays is int days && days > 0)
                {
                    foreach (var it in mine.Where(i => !i.Protected && (now - i.Date).TotalDays > days).ToList())
                    { Delete(it, $"older than {days} days"); mine.Remove(it); }
                }
                if (maxGb is double gb && gb > 0)
                {
                    var cap = (long)(gb * 1e9);
                    var total = mine.Sum(i => i.Bytes);
                    foreach (var it in mine.Where(i => !i.Protected).ToList())
                    {
                        if (total <= cap) break;
                        Delete(it, $"over {gb:0.#} GB cap"); total -= it.Bytes;
                    }
                    if (total > cap) details.Add($"{name}: still {total / 1e9:0.0} GB over a {gb:0.#} GB cap; everything left is protected");
                }
                items.RemoveAll(i => i.Profile == name && !File.Exists(i.Path) && !Directory.Exists(i.Path));
            }

            if (clips + sessions > 0) _log.LogInformation("retention: removed {c} clips, {s} sessions, {mb:0} MB", clips, sessions, freed / 1e6);
            return new Result(clips, sessions, freed, details);
        }
        finally { _lock.Release(); }
    }

    private static long DirBytes(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }); }
        catch { return 0; }
    }
}
