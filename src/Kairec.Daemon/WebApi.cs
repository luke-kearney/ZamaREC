using System.Text;
using System.Text.Json;
using Kairec.Core.Config;
using Kairec.Core.Export;
using Kairec.Core.Recording;
using Kairec.Core.Sessions;

namespace Kairec.Daemon;

public static class WebApi
{
    public sealed record ExportRequest(double Start, double End, string? Label, bool Exact);
    public sealed record AroundRequest(string MarkerId, double Before, double After, string? Label, bool Exact);
    public sealed record BatchRequest(string Type, double Before, double After, bool Exact);
    public sealed record MarkRequest(string? Label);
    public sealed record ConfigTextRequest(string Text);

    public static void MapWebApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (RecordingCoordinator rec, KairecConfig cfg) =>
        {
            var s = rec.Status();
            return Results.Ok(new { s.State, s.Profile, s.Mode, SessionId = rec.ActiveSessionId, s.ElapsedSeconds, s.RecordedSeconds, s.WindowSeconds, s.MarkerCount, s.PendingMarks,
                Profiles = cfg.Profiles.Select(p => new { p.Name, p.Mode }) });
        });

        api.MapPost("/mark", async (MarkRequest req, RecordingCoordinator rec, CancellationToken ct) =>
            Results.Ok(await rec.MarkAsync(req.Label, ct)));

        // ------------------------------------------------------------ config

        api.MapGet("/config", async (ConfigService cs, KairecConfig cfg, RecordingCoordinator rec, CancellationToken ct) => Results.Ok(new
        {
            cs.Path, Text = cs.ReadText(), Config = cfg, Pending = cs.HasPending, SessionRunning = rec.IsActive,
            Monitors = await GsrInfo.MonitorsAsync(ct), AudioDevices = await GsrInfo.AudioDevicesAsync(ct),
            Codecs = new[] { "h264", "hevc", "av1" },
        }));

        api.MapPut("/config", (ConfigTextRequest req, ConfigService cs, RecordingCoordinator rec) =>
        {
            try { return Results.Ok(new { Ok = true, Result = cs.SaveText(req.Text, rec.IsActive) }); }
            catch (Exception ex) { return Results.BadRequest(new { Ok = false, Error = ex.Message }); }
        });

        api.MapPut("/config/structured", ([Microsoft.AspNetCore.Mvc.FromBody] KairecConfig cfg, ConfigService cs, RecordingCoordinator rec) =>
        {
            try { return Results.Ok(new { Ok = true, Result = cs.SaveStructured(cfg, rec.IsActive), Text = cs.ReadText() }); }
            catch (Exception ex) { return Results.BadRequest(new { Ok = false, Error = ex.Message }); }
        });

        // ------------------------------------------------------------ sessions

        api.MapGet("/sessions", (SessionCatalog cat, RecordingCoordinator rec) =>
            Results.Ok(cat.Sessions().Select(s => new
            {
                s.Id, s.Info.Profile, s.Info.Mode, s.Info.Map, s.Info.StartedAt, s.Info.EndedAt, s.Info.DurationSeconds,
                s.MarkerCount, ClipCount = s.Info.Clips.Count, Live = s.Id == rec.ActiveSessionId,
            })));

        api.MapGet("/sessions/{id}", (string id, SessionCatalog cat, RecordingCoordinator rec) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var live = dir == rec.ActiveSessionDir;
            var store = live ? rec.ActiveSession! : SessionStore.Load(dir);
            var index = live ? rec.ActiveSegments : (store.Info.Mode == "timeline" ? new SegmentIndex(dir) : null);
            var segs = index?.All ?? [];
            var available = index?.Available(includeTail: false).Select(s => s.Seq).ToHashSet() ?? new HashSet<int>();
            var recorded = index?.TotalSeconds ?? store.Info.DurationSeconds ?? 0;
            var avail = segs.Where(s => available.Contains(s.Seq)).ToList();
            return Results.Ok(new
            {
                store.Info.Id, store.Info.Profile, store.Info.Mode, store.Info.Map, store.Info.StartedAt, store.Info.EndedAt, Live = live,
                RecordedSeconds = recorded,
                WindowStart = avail.Count > 0 ? avail[0].Start : 0.0,
                WindowEnd = avail.Count > 0 ? avail[^1].Start + avail[^1].Duration : 0.0,
                Segments = segs.Select(s => new { s.Seq, s.File, s.Start, s.Duration, Available = available.Contains(s.Seq) }),
                Markers = store.Markers.Select(m => new { m.Id, m.Type, m.Label, m.TVideo, m.At, m.Starred, m.Clip, m.Data }),
                store.Info.Clips,
            });
        });

        // Playlist of the segments currently on disk. hls.js plays this like a live stream while recording.
        api.MapGet("/sessions/{id}/playlist.m3u8", (string id, SessionCatalog cat, RecordingCoordinator rec) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var live = dir == rec.ActiveSessionDir;
            var index = live ? rec.ActiveSegments : new SegmentIndex(dir);
            if (index is null) return Results.NotFound();
            var segs = index.Available(includeTail: false);
            var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n");
            sb.Append("#EXT-X-TARGETDURATION:").Append((int)Math.Ceiling(segs.Count == 0 ? 5 : segs.Max(s => s.Duration))).Append('\n');
            sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(segs.Count == 0 ? 0 : segs[0].Seq).Append('\n');
            if (!live) sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
            foreach (var s in segs)
                sb.Append("#EXTINF:").Append(s.Duration.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n").Append("seg/").Append(s.File).Append('\n');
            if (!live) sb.Append("#EXT-X-ENDLIST\n");
            return Results.Text(sb.ToString(), "application/vnd.apple.mpegurl");
        });

        api.MapGet("/sessions/{id}/seg/{file}", (string id, string file, SessionCatalog cat) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null || file.Contains('/') || file.Contains("..")) return Results.NotFound();
            var path = Path.Combine(dir, file);
            return File.Exists(path) ? Results.File(path, "video/mp2t", enableRangeProcessing: true) : Results.NotFound();
        });

        api.MapPost("/sessions/{id}/markers/{mid}/star", (string id, string mid, SessionCatalog cat, RecordingCoordinator rec) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var store = dir == rec.ActiveSessionDir ? rec.ActiveSession! : SessionStore.Load(dir);
            var m = store.Markers.FirstOrDefault(x => x.Id == mid);
            if (m is null) return Results.NotFound();
            m.Starred = !m.Starred;
            store.Save();
            return Results.Ok(new { m.Id, m.Starred });
        });

        api.MapPost("/sessions/{id}/export", async (string id, ExportRequest req, SessionCatalog cat, RecordingCoordinator rec, KairecConfig cfg, ILogger<Program> log, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var r = await ExportRangeAsync(dir, req.Start, req.End, req.Label, req.Exact, "export", cat, rec, cfg, log, ct);
            return r.Ok ? Results.Ok(r) : Results.BadRequest(r);
        });

        api.MapPost("/sessions/{id}/export-around", async (string id, AroundRequest req, SessionCatalog cat, RecordingCoordinator rec, KairecConfig cfg, ILogger<Program> log, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var store = dir == rec.ActiveSessionDir ? rec.ActiveSession! : SessionStore.Load(dir);
            var m = store.Markers.FirstOrDefault(x => x.Id == req.MarkerId);
            if (m?.TVideo is not double t) return Results.NotFound();
            var r = await ExportRangeAsync(dir, Math.Max(0, t - req.Before), t + req.After, req.Label ?? m.Label ?? m.Type, req.Exact, "export", cat, rec, cfg, log, ct);
            return r.Ok ? Results.Ok(r) : Results.BadRequest(r);
        });

        api.MapPost("/sessions/{id}/export-batch", async (string id, BatchRequest req, SessionCatalog cat, RecordingCoordinator rec, KairecConfig cfg, ILogger<Program> log, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var store = dir == rec.ActiveSessionDir ? rec.ActiveSession! : SessionStore.Load(dir);
            var results = new List<object>();
            foreach (var m in store.Markers.Where(x => x.Type == req.Type && x.TVideo is not null).ToList())
            {
                var t = m.TVideo!.Value;
                var r = await ExportRangeAsync(dir, Math.Max(0, t - req.Before), t + req.After, m.Label ?? m.Type, req.Exact, "export", cat, rec, cfg, log, ct);
                results.Add(new { Marker = m.Id, r.Ok, r.Path, r.Error });
            }
            return Results.Ok(results);
        });

        // ------------------------------------------------------------ clips

        api.MapGet("/clips", (SessionCatalog cat) =>
            Results.Ok(cat.Clips().Select(c => new
            {
                c.Profile, c.File, c.Bytes, c.Info.Label, c.Info.Source, c.Info.SessionId, c.Info.SessionOffset, c.Info.DurationSeconds,
                c.Info.CreatedAt, c.Info.Starred, c.Info.ExpiresAt, Markers = c.Info.Markers,
            })));

        api.MapGet("/clips/{profile}/{file}", (string profile, string file, SessionCatalog cat) =>
        {
            var p = cat.ClipPath(profile, file);
            return p is null ? Results.NotFound() : Results.File(p, "video/mp4", enableRangeProcessing: true);
        });

        api.MapPost("/clips/{profile}/{file}/star", (string profile, string file, SessionCatalog cat) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var info = ReadClipInfo(p, profile);
            info.Starred = !info.Starred;
            if (info.Starred) info.ExpiresAt = null;
            SessionCatalog.SaveClipInfo(p, info);
            return Results.Ok(new { info.Starred });
        });

        api.MapDelete("/clips/{profile}/{file}", (string profile, string file, SessionCatalog cat) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            File.Delete(p);
            if (File.Exists(p + ".json")) File.Delete(p + ".json");
            return Results.Ok();
        });

        api.MapPost("/clips/{profile}/{file}/export", async (string profile, string file, ExportRequest req, SessionCatalog cat, KairecConfig cfg, ILogger<Program> log, CancellationToken ct) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var src = ReadClipInfo(p, profile);
            var target = ClipNaming.NewPath(cfg.ClipsDir, profile, req.Label ?? src.Label);
            var r = await ClipExporter.ExportFromFileAsync(p, req.Start, req.End, target, req.Exact, log, ct);
            if (!r.Ok) return Results.BadRequest(new { r.Ok, r.Error });
            var info = new ClipInfo
            {
                Profile = profile, Label = req.Label ?? src.Label, Source = "export", SessionId = src.SessionId,
                SessionOffset = src.SessionOffset is double o ? o + req.Start : null, DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now,
                Markers = src.Markers.Where(m => m.T >= req.Start && m.T <= req.End).Select(m => new ClipMarker { T = m.T - req.Start, Type = m.Type, Label = m.Label, Data = m.Data }).ToList(),
            };
            SessionCatalog.SaveClipInfo(target, info);
            return Results.Ok(new { r.Ok, Path = target, File = Path.GetFileName(target), r.Duration });
        });
    }

    private static ClipInfo ReadClipInfo(string clipPath, string profile)
    {
        try
        {
            if (File.Exists(clipPath + ".json"))
                return JsonSerializer.Deserialize(File.ReadAllText(clipPath + ".json"), SessionJson.Default.ClipInfo) ?? new ClipInfo { Profile = profile };
        }
        catch { }
        return new ClipInfo { Profile = profile, CreatedAt = File.GetCreationTime(clipPath) };
    }

    public sealed record ExportResult(bool Ok, string? Path, string? File, double? Duration, string? Error);

    private static async Task<ExportResult> ExportRangeAsync(string dir, double start, double end, string? label, bool exact, string source,
        SessionCatalog cat, RecordingCoordinator rec, KairecConfig cfg, ILogger log, CancellationToken ct)
    {
        var live = dir == rec.ActiveSessionDir;
        var store = live ? rec.ActiveSession! : SessionStore.Load(dir);
        var index = live ? rec.ActiveSegments : new SegmentIndex(dir);
        if (index is null) return new ExportResult(false, null, null, null, "session has no timeline");
        index.Refresh();
        var target = ClipNaming.NewPath(cfg.ClipsDir, store.Info.Profile, label);
        var r = await ClipExporter.ExportAsync(dir, index, start, end, target, exact, log, ct);
        if (!r.Ok) return new ExportResult(false, null, null, null, r.Error);
        var actualStart = end - (r.Duration ?? (end - start));
        var info = new ClipInfo
        {
            Profile = store.Info.Profile, Label = label, Source = source, SessionId = store.Info.Id, SessionOffset = actualStart,
            DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now,
            Markers = store.Markers.Where(m => m.TVideo is double t && t >= actualStart && t <= end)
                .Select(m => new ClipMarker { T = m.TVideo!.Value - actualStart, Type = m.Type, Label = m.Label, Data = m.Data }).ToList(),
        };
        SessionCatalog.SaveClipInfo(target, info);
        store.Info.Clips.Add(Path.GetFileName(target));
        store.Save();
        log.LogInformation("exported {start:0.0}-{end:0.0}s of {id} -> {path}", start, end, store.Info.Id, target);
        return new ExportResult(true, target, Path.GetFileName(target), r.Duration, null);
    }
}
