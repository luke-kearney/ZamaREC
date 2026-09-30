using System.Text;
using System.Text.Json;
using Kairec.Core.Config;
using Kairec.Core.Export;
using Kairec.Core.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Kairec.Core.Recording;
using Kairec.Core.Sessions;
using Kairec.Core.Telemetry;

namespace Kairec.Daemon;

public static class WebApi
{
    public sealed record ExportRequest(double Start, double End, string? Label, bool Exact);
    public sealed record AroundRequest(string MarkerId, double Before, double After, string? Label, bool Exact);
    public sealed record BatchRequest(string Type, double Before, double After, bool Exact);
    public sealed record MarkRequest(string? Label);
    public sealed record ConfigTextRequest(string Text);
    public sealed record PauseRequest(string? Mode, double? Hours);

    public static void MapWebApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (RecordingCoordinator rec, KairecConfig cfg, GsiService gsi, PauseControl pause) =>
        {
            var s = rec.Status();
            var u = rec.Usage();
            var ps = pause.Current;
            var ramDir = Paths.Expand(cfg.Timeline.RamDir);
            return Results.Ok(new { s.State, s.Profile, s.Mode, s.Unconfirmed, SessionId = rec.ActiveSessionId, s.ElapsedSeconds, s.RecordedSeconds, s.WindowSeconds, s.MarkerCount, s.PendingMarks,
                GsiLastSeconds = gsi.SecondsSinceLastPayload, GsiMap = gsi.CurrentMap, GsiCfg = gsi.CfgPath,
                Usage = new { u.CpuPercent, u.RssBytes, u.Processes, RamFsBytes = ProcessUsage.DirBytes(ramDir), RamFsFree = FreeBytes(ramDir), Cores = Environment.ProcessorCount },
                Pause = ps.Mode == "off" ? null : new { ps.Mode, ps.Until, Text = PauseControl.Describe(ps) },
                Profiles = cfg.Profiles.Select(p => new { p.Name, p.Mode }) });
        });

        api.MapPost("/pause", async (PauseRequest req, PauseControl pause, RecordingCoordinator rec, CancellationToken ct) =>
        {
            var msg = pause.Pause(req.Mode ?? "session", req.Hours, rec.ActiveGamePid, rec.Status().Profile);
            if (rec.IsActive) await rec.StopAsync("paused", ct);
            return Results.Ok(new { Ok = true, Message = "recording paused " + msg });
        });
        api.MapPost("/resume", (PauseControl pause) => { pause.Resume(); return Results.Ok(new { Ok = true, Message = "recording on" }); });

        api.MapPost("/retention/run", (RetentionService ret) => Results.Ok(ret.RunNow()));

        api.MapPost("/ram/clear", (RecordingCoordinator rec) => { var (n, bytes) = rec.ClearRam(); return Results.Ok(new { Sessions = n, Bytes = bytes }); });

        // CS2 posts its game state here (see gamestate_integration_kairec.cfg). Timestamped on arrival.
        app.MapPost("/gsi", async (HttpRequest req, GsiService gsi, CancellationToken ct) =>
        {
            var arrival = Core.MonotonicClock.NowMicros();
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var ok = await gsi.HandleAsync(doc.RootElement, arrival, ct);
            return ok ? Results.Ok() : Results.Unauthorized();
        });

        api.MapPost("/mark", async (MarkRequest req, RecordingCoordinator rec, CancellationToken ct) =>
            Results.Ok(await rec.MarkAsync(req.Label, ct)));

        // ------------------------------------------------------------ log + stats

        api.MapGet("/events", (EventLog log, int? limit, string? kind) => Results.Ok(log.Tail(Math.Clamp(limit ?? 200, 1, 2000), string.IsNullOrEmpty(kind) ? null : kind)));

        api.MapGet("/stats", (StatsStore stats, SessionCatalog cat, RecordingCoordinator rec, KairecConfig cfg) =>
        {
            var clips = cat.Clips();
            var clipBytesBySession = clips.Where(c => c.Info.SessionId is not null).GroupBy(c => c.Info.SessionId!).ToDictionary(g => g.Key, g => (Bytes: g.Sum(c => c.Bytes), Count: g.Count()));
            var sessions = cat.Sessions().Select(s =>
            {
                var live = s.Id == rec.ActiveSessionId;
                var toRam = s.Info.Mode == "buffer" || s.Info.MediaDir is not null;
                var recorded = live ? rec.ActiveRecordedBytes : s.Info.RecordedBytes;
                clipBytesBySession.TryGetValue(s.Id, out var cb);
                return new { s.Id, s.Info.Profile, s.Info.Mode, Storage = toRam ? "ram" : "disk", s.Info.StartedAt, s.Info.EndedAt, s.Info.DurationSeconds, Live = live,
                    RecordedBytes = recorded, Estimated = s.Info.BytesEstimated || (live && s.Info.Mode == "buffer"), ClipBytes = cb.Bytes, ClipCount = cb.Count, s.MarkerCount, s.Info.FootageGone };
            }).ToList();
            long dirSize(string d) { try { return Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; } catch { return 0; } }
            long ramUsed = 0; try { var rd = Paths.Expand(cfg.Timeline.RamDir); ramUsed = dirSize(rd); } catch { }
            long free = 0; try { free = new DriveInfo(cfg.RootDir).AvailableFreeSpace; } catch { }
            var c = stats.Counters;
            var liveRam = rec.ActiveSessionId is not null && sessions.FirstOrDefault(s => s.Live) is { } ls && ls.Storage == "ram" ? ls.RecordedBytes : 0;
            var liveDisk = rec.ActiveSessionId is not null && sessions.FirstOrDefault(s => s.Live) is { } ld && ld.Storage == "disk" ? ld.RecordedBytes : 0;
            return Results.Ok(new
            {
                Totals = new { c.Sessions, c.SecondsRecorded, DiskBytes = c.DiskBytes + liveDisk, RamBytes = c.RamBytes + liveRam, c.ClipBytes, c.Clips, c.Marks, c.Saves, c.Exports },
                OnDisk = new { SessionsBytes = dirSize(cfg.SessionsDir), ClipsBytes = dirSize(cfg.ClipsDir), FreeBytes = free, RamDirBytes = ramUsed },
                Sessions = sessions,
            });
        });

        // Running processes, for the profile picker in Settings. Read from /proc on demand only.
        api.MapGet("/processes", () =>
        {
            var self = Environment.ProcessId;
            var list = new List<object>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == self) continue;
                try
                {
                    var comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n');
                    var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                    if (cmd.Length == 0) continue;   // kernel threads
                    var uid = File.ReadLines(Path.Combine(dir, "status")).FirstOrDefault(l => l.StartsWith("Uid:"))?.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                    if (uid != Environment.GetEnvironmentVariable("UID") && uid != GetUid()) continue;   // only my own processes
                    list.Add(new { Pid = pid, Comm = comm, Cmdline = cmd.Length > 160 ? cmd[..160] + "…" : cmd });
                }
                catch { }
            }
            return Results.Ok(list.OrderBy(x => ((dynamic)x).Comm).ToList());
        });

        // ------------------------------------------------------------ config

        api.MapGet("/config", async (ConfigService cs, KairecConfig cfg, RecordingCoordinator rec, CancellationToken ct) => Results.Ok(new
        {
            cs.Path, Text = cs.ReadText(), Config = cfg, Pending = cs.HasPending, SessionRunning = rec.IsActive,
            RamFree = FreeBytes(Paths.Expand(cfg.Timeline.RamDir)), RamTotal = TotalBytes(Paths.Expand(cfg.Timeline.RamDir)),
            DiskFree = FreeBytes(cfg.RootDir), MemTotal = MemTotalBytes(),
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

        // ------------------------------------------------------------ game presets and banners

        api.MapGet("/presets", () => Results.Ok(GamePresets.All.Select(g => new { g.Id, g.Name, g.Match, g.MatchCmdline, g.Events, g.Mode, g.SteamAppId, g.BannerUrl })));

        // banners are per profile: uploaded image > profile banner_url > steam_app_id (own or preset's) > Steam search by name
        api.MapGet("/banners/{profile}", async (string profile, BannerService banners, CancellationToken ct) =>
        {
            var path = await banners.PathAsync(profile, ct);
            if (path is null) return Results.NotFound();
            var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
            return Results.File(path, type, lastModified: File.GetLastWriteTimeUtc(path));
        });
        api.MapGet("/banners/{profile}/source", (string profile, BannerService banners) => Results.Ok(new { Source = banners.Source(profile), Custom = banners.CustomPath(profile) is not null }));
        api.MapPut("/banners/{profile}", async (string profile, HttpRequest req, BannerService banners, CancellationToken ct) =>
        {
            if (req.ContentLength is > 8_000_000) return Results.BadRequest(new { Error = "image larger than 8 MB" });
            try { await banners.SaveCustomAsync(profile, req.Body, req.ContentType ?? "", ct); return Results.Ok(new { Ok = true }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { Error = ex.Message }); }
        });
        api.MapDelete("/banners/{profile}", (string profile, BannerService banners) => Results.Ok(new { Removed = banners.RemoveCustom(profile) }));
        api.MapPost("/banners/{profile}/research", (string profile, BannerService banners) => { banners.ForgetSearch(profile); return Results.Ok(new { Ok = true }); });

        // ------------------------------------------------------------ steam games: profiled, unconfirmed, ignored

        // Every Steam game seen launched, with what kairec does about it. "running" reads /proc on request only.
        api.MapGet("/games", (KairecConfig cfg, SeenGames seen, RecordingCoordinator rec) =>
        {
            var running = new ProcessWatcher([], NullLogger.Instance).SteamLaunches().Select(l => l.AppId).ToHashSet();
            var q = cfg.FindQuality(cfg.AutoGames.Quality) ?? cfg.FindQuality(cfg.Capture.Quality);
            var kbps = q?.BitrateKbps ?? cfg.Capture.BitrateKbps;
            return Results.Ok(new
            {
                Auto = new { cfg.AutoGames.Enabled, cfg.AutoGames.RamGb, Quality = q?.Name, BitrateKbps = kbps, WindowMinutes = cfg.AutoGames.WindowMinutes(kbps) },
                Games = seen.All.Select(g =>
                {
                    var profile = cfg.ProfileForSteamApp(g.AppId) ?? cfg.FindProfile(g.Name) ?? (g.Profile is null ? null : cfg.FindProfile(g.Profile));
                    return new
                    {
                        g.AppId, g.Name, g.FirstSeen, g.LastSeen, g.Launches, Profile = profile?.Name,
                        State = profile is not null ? "profile" : cfg.AutoGames.Ignores(g.AppId) ? "ignored" : "unconfirmed",
                        Running = running.Contains(g.AppId), Recording = rec.ActiveSteamAppId == g.AppId,
                    };
                }),
            });
        });

        // Turns an unconfirmed game into a profile with the settings it has been recorded with, or hands the
        // app id to an existing profile of the same name.
        api.MapPost("/games/{appId:long}/keep", (long appId, KairecConfig cfg, SeenGames seen, ConfigService cs, RecordingCoordinator rec) =>
        {
            if (seen.Find(appId) is not { } game || appId > int.MaxValue) return Results.NotFound();
            if ((cfg.ProfileForSteamApp(appId) ?? (game.Profile is null ? null : cfg.FindProfile(game.Profile))) is { } owner) return Results.Ok(new { Ok = true, Profile = owner.Name, Result = "exists" });
            try
            {
                var next = ConfigLoader.Parse(ConfigLoader.Serialize(cfg));
                if (next.FindProfile(game.Name) is { } same) same.SteamAppId = (int)appId;
                else next.Profiles.Add(next.AutoProfile((int)appId, game.Name));
                next.AutoGames.Ignore.Remove(appId);
                return Results.Ok(new { Ok = true, Profile = game.Name, Result = cs.SaveStructured(next, rec.IsActive) });
            }
            catch (Exception ex) { return Results.BadRequest(new { Ok = false, Error = ex.Message }); }
        });

        // Never record this game on its own. A session recording it right now is stopped.
        api.MapPost("/games/{appId:long}/ignore", async (long appId, KairecConfig cfg, ConfigService cs, RecordingCoordinator rec, CancellationToken ct) =>
        {
            try
            {
                if (rec.ActiveUnconfirmed && rec.ActiveSteamAppId == appId) await rec.StopAsync("game ignored", ct);
                var next = ConfigLoader.Parse(ConfigLoader.Serialize(cfg));
                if (!next.AutoGames.Ignore.Contains(appId)) next.AutoGames.Ignore.Add(appId);
                return Results.Ok(new { Ok = true, Result = cs.SaveStructured(next, rec.IsActive) });
            }
            catch (Exception ex) { return Results.BadRequest(new { Ok = false, Error = ex.Message }); }
        });

        api.MapDelete("/games/{appId:long}/ignore", (long appId, KairecConfig cfg, ConfigService cs, RecordingCoordinator rec) =>
        {
            try
            {
                var next = ConfigLoader.Parse(ConfigLoader.Serialize(cfg));
                next.AutoGames.Ignore.Remove(appId);
                return Results.Ok(new { Ok = true, Result = cs.SaveStructured(next, rec.IsActive) });
            }
            catch (Exception ex) { return Results.BadRequest(new { Ok = false, Error = ex.Message }); }
        });

        api.MapGet("/sessions", (SessionCatalog cat, RecordingCoordinator rec, KairecConfig cfg) =>
            Results.Ok(cat.Sessions().Select(s => new
            {
                s.Id, s.Info.Profile, s.Info.Mode, s.Info.Map, s.Info.StartedAt, s.Info.EndedAt, s.Info.DurationSeconds,
                s.MarkerCount, ClipCount = s.Info.Clips.Count, Live = s.Id == rec.ActiveSessionId,
                Ram = s.Info.MediaDir is not null, s.Info.FootageGone,
                s.Info.SteamAppId, Unconfirmed = s.Info.SteamAppId is not null && cfg.FindProfile(s.Info.Profile) is null,
                Game = GamePresets.ResolveByProfileName(cfg, s.Info.Profile)?.Id,
                GameName = GamePresets.ResolveByProfileName(cfg, s.Info.Profile)?.Name ?? s.Info.Profile,
            })));

        api.MapGet("/sessions/{id}", (string id, SessionCatalog cat, RecordingCoordinator rec, KairecConfig cfg) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var live = dir == rec.ActiveSessionDir;
            var store = live ? rec.ActiveSession! : SessionStore.Load(dir);
            var index = live ? rec.ActiveSegments : (store.Info.Mode == "timeline" ? new SegmentIndex(dir, store.MediaDir) : null);
            var segs = index?.All ?? [];
            var available = index?.Available(includeTail: false).Select(s => s.Seq).ToHashSet() ?? new HashSet<int>();
            var recorded = index?.TotalSeconds ?? store.Info.DurationSeconds ?? 0;
            var avail = segs.Where(s => available.Contains(s.Seq)).ToList();
            return Results.Ok(new
            {
                store.Info.Id, store.Info.Profile, store.Info.Mode, store.Info.Map, store.Info.StartedAt, store.Info.EndedAt, Live = live,
                store.Info.SteamAppId, Unconfirmed = store.Info.SteamAppId is not null && cfg.FindProfile(store.Info.Profile) is null,
                Game = GamePresets.ResolveByProfileName(cfg, store.Info.Profile)?.Id,
                Ram = store.Info.MediaDir is not null, store.Info.FootageGone,
                RecordedSeconds = recorded,
                WindowStart = avail.Count > 0 ? avail[0].Start : 0.0,
                WindowEnd = avail.Count > 0 ? avail[^1].Start + avail[^1].Duration : 0.0,
                Segments = segs.Select(s => new { s.Seq, s.File, s.Start, s.Duration, Available = available.Contains(s.Seq) }),
                Markers = store.Markers.Select(m => new { m.Id, m.Type, m.Label, m.TVideo, m.At, m.Starred, m.Clip, m.Data }),
                store.Info.Clips,
                ClipDetails = store.Info.Clips.Select(f => (File: f, Path: cat.ClipPath(store.Info.Profile, f)))
                    .Where(x => x.Path is not null)
                    .Select(x => { var i = ReadClipInfo(x.Path!, store.Info.Profile); return new { x.File, i.Label, i.Source, i.SessionOffset, i.DurationSeconds, i.Starred, i.ExpiresAt, i.Markers }; }),
                ElapsedSeconds = live ? (Core.MonotonicClock.NowMicros() - store.Info.SpawnMonoUs) / 1_000_000.0 : (double?)null,
            });
        });

        // Playlist of the segments currently on disk. hls.js plays this like a live stream while recording.
        api.MapGet("/sessions/{id}/playlist.m3u8", (string id, SessionCatalog cat, RecordingCoordinator rec) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var live = dir == rec.ActiveSessionDir;
            var index = live ? rec.ActiveSegments : new SegmentIndex(dir, SessionStore.Load(dir).MediaDir);
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

        api.MapGet("/sessions/{id}/seg/{file}", (string id, string file, SessionCatalog cat, RecordingCoordinator rec) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null || file.Contains('/') || file.Contains("..")) return Results.NotFound();
            var mediaDir = dir == rec.ActiveSessionDir ? rec.ActiveSession!.MediaDir : SessionStore.Load(dir).MediaDir;
            var path = Path.Combine(mediaDir, file);
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

        api.MapGet("/clips", (SessionCatalog cat, KairecConfig cfg) =>
            Results.Ok(cat.Clips().Select(c => new
            {
                c.Profile, c.File, c.Bytes, c.Info.Label, c.Info.Source, c.Info.SessionId, c.Info.SessionOffset, c.Info.DurationSeconds,
                c.Info.CreatedAt, RecordedAt = c.At, c.Info.Starred, c.Info.ExpiresAt, Markers = c.Info.Markers,
                Game = GamePresets.ResolveByProfileName(cfg, c.Profile)?.Id,
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

        api.MapPost("/clips/{profile}/{file}/export", async (string profile, string file, ExportRequest req, SessionCatalog cat, KairecConfig cfg, RecordingCoordinator rec, ILogger<Program> log, CancellationToken ct) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var src = ReadClipInfo(p, profile);
            // The new clip is dated by its footage, not by when it was cut: the source's end, less what was left off.
            var recordedAt = cat.FootageTime(src).AddSeconds(src.DurationSeconds is double len ? Math.Min(0, req.End - len) : 0);
            var target = ClipNaming.NewPath(cfg.ClipsDir, profile, req.Label ?? src.Label, at: recordedAt);
            var r = await ClipExporter.ExportFromFileAsync(p, req.Start, req.End, target, req.Exact, log, ct);
            if (!r.Ok) return Results.BadRequest(new { r.Ok, r.Error });
            var info = new ClipInfo
            {
                Profile = profile, Label = req.Label ?? src.Label, Source = "export", SessionId = src.SessionId,
                SessionOffset = src.SessionOffset is double o ? o + req.Start : null, DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now, RecordedAt = recordedAt,
                Markers = src.Markers.Where(m => m.T >= req.Start && m.T <= req.End).Select(m => new ClipMarker { T = m.T - req.Start, Type = m.Type, Label = m.Label, Data = m.Data }).ToList(),
            };
            SessionCatalog.SaveClipInfo(target, info);
            rec.RecordExport(profile, src.SessionId, target, r.Duration);
            return Results.Ok(new { r.Ok, Path = target, File = Path.GetFileName(target), r.Duration });
        });
    }

    private static string? _uid;
    private static string GetUid()
    {
        if (_uid is not null) return _uid;
        try { _uid = File.ReadLines("/proc/self/status").First(l => l.StartsWith("Uid:")).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1]; } catch { _uid = ""; }
        return _uid;
    }

    private static long FreeBytes(string path)
    {
        try { var p = path; while (!Directory.Exists(p)) p = Path.GetDirectoryName(p) ?? "/"; return new DriveInfo(p).AvailableFreeSpace; } catch { return 0; }
    }
    private static long TotalBytes(string path)
    {
        try { var p = path; while (!Directory.Exists(p)) p = Path.GetDirectoryName(p) ?? "/"; return new DriveInfo(p).TotalSize; } catch { return 0; }
    }
    private static long MemTotalBytes()
    {
        try { foreach (var l in File.ReadLines("/proc/meminfo")) if (l.StartsWith("MemTotal:")) return long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024; } catch { }
        return 0;
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
        var index = live ? rec.ActiveSegments : new SegmentIndex(dir, store.MediaDir);
        if (index is null) return new ExportResult(false, null, null, null, "session has no timeline");
        index.Refresh();
        var recordedAt = store.Info.TimeAt(end);   // dated by its footage, not by when it was cut
        var target = ClipNaming.NewPath(cfg.ClipsDir, store.Info.Profile, label, at: recordedAt);
        var r = await ClipExporter.ExportAsync(dir, index, start, end, target, exact, log, ct);
        if (!r.Ok) return new ExportResult(false, null, null, null, r.Error);
        var actualStart = end - (r.Duration ?? (end - start));
        var info = new ClipInfo
        {
            Profile = store.Info.Profile, Label = label, Source = source, SessionId = store.Info.Id, SessionOffset = actualStart,
            DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now, RecordedAt = recordedAt,
            Markers = store.Markers.Where(m => m.TVideo is double t && t >= actualStart && t <= end)
                .Select(m => new ClipMarker { T = m.TVideo!.Value - actualStart, Type = m.Type, Label = m.Label, Data = m.Data }).ToList(),
        };
        SessionCatalog.SaveClipInfo(target, info);
        store.Info.Clips.Add(Path.GetFileName(target));
        store.Save();
        log.LogInformation("exported {start:0.0}-{end:0.0}s of {id} -> {path}", start, end, store.Info.Id, target);
        rec.RecordExport(store.Info.Profile, store.Info.Id, target, r.Duration);
        return new ExportResult(true, target, Path.GetFileName(target), r.Duration, null);
    }
}
