using System.Text;
using System.Text.Json;
using ZamaRec.Core.Config;
using ZamaRec.Core.Export;
using ZamaRec.Core.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using ZamaRec.Core.Recording;
using ZamaRec.Core.Sessions;
using ZamaRec.Core.Telemetry;

namespace ZamaRec.Daemon;

public static class WebApi
{
    /// <param name="Mix">Track name to level: remake the clip's mix from the separate tracks at these levels.</param>
    /// <param name="MaxMb">Make a re-encoded copy that fits under this many megabytes, for posting somewhere with a size limit.</param>
    public sealed record ExportRequest(double Start, double End, string? Label, bool Exact, Dictionary<string, double>? Mix = null, double? MaxMb = null);
    public sealed record LabelRequest(string? Label);
    public sealed record ShareRequestBody(string? Message);
    public sealed record AroundRequest(string MarkerId, double Before, double After, string? Label, bool Exact, Dictionary<string, double>? Mix = null);
    public sealed record BatchRequest(string Type, double Before, double After, bool Exact, Dictionary<string, double>? Mix = null);
    public sealed record MarkRequest(string? Label);
    public sealed record ConfigTextRequest(string Text);
    public sealed record PauseRequest(string? Mode, double? Hours);

    public static void MapWebApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (RecordingCoordinator rec, ZamaRecConfig cfg, GsiService gsi, PauseControl pause, ShareJobs share) =>
        {
            var s = rec.Status();
            var u = rec.Usage();
            var ps = pause.Current;
            var ramDir = Paths.Expand(cfg.Timeline.RamDir);
            return Results.Ok(new { s.State, s.Profile, s.Mode, s.Unconfirmed, SessionId = rec.ActiveSessionId, s.ElapsedSeconds, s.RecordedSeconds, s.WindowSeconds, s.MarkerCount, s.PendingMarks,
                GsiLastSeconds = gsi.SecondsSinceLastPayload, GsiMap = gsi.CurrentMap, GsiCfg = gsi.CfgPath,
                Usage = new { u.CpuPercent, u.RssBytes, u.Processes, RamFsBytes = ProcessUsage.DirBytes(ramDir), RamFsFree = FreeBytes(ramDir), Cores = Environment.ProcessorCount },
                Pause = ps.Mode == "off" ? null : new { ps.Mode, ps.Until, Text = PauseControl.Describe(ps) },
                ShareTargets = share.Targets(),
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

        // CS2 posts its game state here (see gamestate_integration_zamarec.cfg). Timestamped on arrival.
        app.MapPost("/gsi", async (HttpRequest req, GsiService gsi, CancellationToken ct) =>
        {
            var arrival = Core.MonotonicClock.NowMicros();
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var ok = await gsi.HandleAsync(doc.RootElement, arrival, ct);
            return ok ? Results.Ok() : Results.Unauthorized();
        });

        api.MapPost("/mark", async (MarkRequest req, RecordingCoordinator rec, CancellationToken ct) =>
            Results.Ok(await rec.MarkAsync(req.Label, ct)));

        // The CLI's request and reply as they go over the control socket (snake_case), for clients without that socket.
        api.MapPost("/control", async (ZamaRec.Protocol.ControlRequest req, ControlSocketServer control, CancellationToken ct) =>
            Results.Json(await control.DispatchAsync(req, ct), ZamaRec.Protocol.ProtocolJson.Default.ControlResponse));

        // ------------------------------------------------------------ log + stats

        api.MapGet("/events", (EventLog log, int? limit, string? kind) => Results.Ok(log.Tail(Math.Clamp(limit ?? 200, 1, 2000), string.IsNullOrEmpty(kind) ? null : kind)));

        api.MapGet("/stats", (StatsStore stats, SessionCatalog cat, RecordingCoordinator rec, ZamaRecConfig cfg) =>
        {
            var clips = cat.Clips();
            var clipBytesBySession = clips.Where(c => c.Info.SessionId is not null).GroupBy(c => c.Info.SessionId!).ToDictionary(g => g.Key, g => (Bytes: g.Sum(c => c.Bytes), Count: g.Count()));
            var sessions = cat.Sessions().Select(s =>
            {
                var live = s.Id == rec.ActiveSessionId;
                var toRam = s.Info.Mode == "buffer" || s.Info.InRam;
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

            // Per folder: what was ever written there (stats.json) next to what is in it now. The folders in use
            // are listed even before anything was written; one that is no longer configured stays with its figures.
            var pathCmp = Core.Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var rootKey = StatsCounters.NormalizeDir(cfg.RootDir);
            var footageKey = cfg.FootageDir is { } fk ? StatsCounters.NormalizeDir(fk) : null;
            var known = stats.PlacesSnapshot().ToDictionary(p => p.Key, p => p.Value, pathCmp);
            known.TryAdd(rootKey, new PlaceCounters());
            if (footageKey is not null) known.TryAdd(footageKey, new PlaceCounters());
            var places = known.Select(p =>
            {
                var isRoot = pathCmp.Equals(p.Key, rootKey);
                var isFootage = footageKey is null ? isRoot : pathCmp.Equals(p.Key, footageKey);
                long driveFree = 0, driveTotal = 0;
                try { var di = new DriveInfo(Path.GetPathRoot(p.Key) is { Length: > 0 } r ? r : p.Key); if (di.IsReady) { driveFree = di.AvailableFreeSpace; driveTotal = di.TotalSize; } } catch { }
                return new
                {
                    Path = p.Key, Drive = Path.GetPathRoot(p.Key),
                    Role = isRoot && isFootage ? "clips and timeline footage" : isRoot ? "clips and session details" : isFootage ? "timeline footage" : "not in use",
                    InUse = isRoot || isFootage, Exists = Directory.Exists(p.Key),
                    p.Value.FootageBytes, p.Value.ClipBytes, p.Value.Sessions, p.Value.Clips, p.Value.FirstUsed, p.Value.LastUsed, p.Value.Seeded,
                    NowBytes = dirSize(p.Key), DriveFree = driveFree, DriveTotal = driveTotal,
                };
            }).OrderByDescending(p => p.InUse).ThenBy(p => p.Path, pathCmp).ToList();
            return Results.Ok(new
            {
                Places = places,
                Totals = new { c.Sessions, c.SecondsRecorded, DiskBytes = c.DiskBytes + liveDisk, RamBytes = c.RamBytes + liveRam, c.ClipBytes, c.Clips, c.Marks, c.Saves, c.Exports },
                OnDisk = new { SessionsBytes = dirSize(cfg.SessionsDir) + (cfg.FootageDir is { } fdir ? dirSize(fdir) : 0),ClipsBytes = dirSize(cfg.ClipsDir), FreeBytes = free, RamDirBytes = ramUsed },
                Sessions = sessions,
            });
        });

        // Running processes, for the profile picker in Settings. Read from /proc (Windows: the process table) on demand only.
        api.MapGet("/processes", () =>
            Results.Ok(ProcessWatcher.ListAll(Environment.ProcessId)
                .Select(p => new { p.Pid, p.Comm, Cmdline = p.Cmdline.Length > 160 ? p.Cmdline[..160] + "…" : p.Cmdline })
                .OrderBy(x => x.Comm).ToList()));

        // ------------------------------------------------------------ config

        api.MapGet("/config", async (ConfigService cs, ZamaRecConfig cfg, RecordingCoordinator rec, CancellationToken ct) => Results.Ok(new
        {
            cs.Path, Text = cs.ReadText(), Config = cfg, Pending = cs.HasPending, SessionRunning = rec.IsActive,
            RamFree = FreeBytes(Paths.Expand(cfg.Timeline.RamDir)), RamTotal = TotalBytes(Paths.Expand(cfg.Timeline.RamDir)),
            DiskFree = FreeBytes(cfg.RootDir), MemTotal = MemTotalBytes(),
            Root = cfg.RootDir, Drives = Drives(),
            FootageDir = cfg.FootageDir, FootageFree = cfg.FootageDir is { } fd ? FreeBytes(fd) : (long?)null,
            Monitors = await GsrInfo.MonitorsAsync(ct), AudioDevices = await GsrInfo.AudioDevicesAsync(ct), AudioApps = await GsrInfo.AudioApplicationsAsync(ct),
            Codecs = new[] { "h264", "hevc", "av1" },
            Platform = Core.Platform.IsWindows ? "windows" : "linux",
        }));

        api.MapPut("/config", (ConfigTextRequest req, ConfigService cs, RecordingCoordinator rec) =>
        {
            try { return Results.Ok(new { Ok = true, Result = cs.SaveText(req.Text, rec.IsActive) }); }
            catch (Exception ex) { return Results.BadRequest(new { Ok = false, Error = ex.Message }); }
        });

        api.MapPut("/config/structured", ([Microsoft.AspNetCore.Mvc.FromBody] ZamaRecConfig cfg, ConfigService cs, RecordingCoordinator rec) =>
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

        // Every Steam game seen launched, with what zamarec does about it. "running" reads /proc on request only.
        api.MapGet("/games", (ZamaRecConfig cfg, SeenGames seen, RecordingCoordinator rec) =>
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
        api.MapPost("/games/{appId:long}/keep", (long appId, ZamaRecConfig cfg, SeenGames seen, ConfigService cs, RecordingCoordinator rec) =>
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
        api.MapPost("/games/{appId:long}/ignore", async (long appId, ZamaRecConfig cfg, ConfigService cs, RecordingCoordinator rec, CancellationToken ct) =>
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

        api.MapDelete("/games/{appId:long}/ignore", (long appId, ZamaRecConfig cfg, ConfigService cs, RecordingCoordinator rec) =>
        {
            try
            {
                var next = ConfigLoader.Parse(ConfigLoader.Serialize(cfg));
                next.AutoGames.Ignore.Remove(appId);
                return Results.Ok(new { Ok = true, Result = cs.SaveStructured(next, rec.IsActive) });
            }
            catch (Exception ex) { return Results.BadRequest(new { Ok = false, Error = ex.Message }); }
        });

        api.MapGet("/sessions", (SessionCatalog cat, RecordingCoordinator rec, ZamaRecConfig cfg) =>
            Results.Ok(cat.Sessions().Select(s => new
            {
                s.Id, s.Info.Profile, s.Info.Mode, s.Info.Map, s.Info.StartedAt, s.Info.EndedAt, s.Info.DurationSeconds,
                s.MarkerCount, ClipCount = s.Info.Clips.Count, Live = s.Id == rec.ActiveSessionId,
                Ram = s.Info.InRam, s.Info.FootageGone,
                s.Info.SteamAppId, Unconfirmed = s.Info.SteamAppId is not null && cfg.FindProfile(s.Info.Profile) is null,
                s.Info.AudioTracks,
                Game = GamePresets.ResolveByProfileName(cfg, s.Info.Profile)?.Id,
                GameName = GamePresets.ResolveByProfileName(cfg, s.Info.Profile)?.Name ?? s.Info.Profile,
            })));

        api.MapGet("/sessions/{id}", (string id, SessionCatalog cat, RecordingCoordinator rec, ZamaRecConfig cfg) =>
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
                store.Info.AudioTracks,
                Game = GamePresets.ResolveByProfileName(cfg, store.Info.Profile)?.Id,
                Ram = store.Info.InRam, store.Info.FootageGone,
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
        IResult Playlist(string id, SessionCatalog cat, RecordingCoordinator rec)
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
        }
        api.MapGet("/sessions/{id}/playlist.m3u8", Playlist);

        // One audio track on its own, for the mixer: the same playlist, its "seg/" entries resolving to the
        // audio-only segments below. Track 0 is the mix, which the video already carries.
        api.MapGet("/sessions/{id}/audio/{track:int}/playlist.m3u8", (string id, int track, SessionCatalog cat, RecordingCoordinator rec) => Playlist(id, cat, rec));
        api.MapGet("/sessions/{id}/audio/{track:int}/seg/{file}", async (string id, int track, string file, SessionCatalog cat, RecordingCoordinator rec, StemService stems, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null || track is < 0 or > 15 || file.Contains('/') || file.Contains("..")) return Results.NotFound();
            var mediaDir = dir == rec.ActiveSessionDir ? rec.ActiveSession!.MediaDir : SessionStore.Load(dir).MediaDir;
            var path = Path.Combine(mediaDir, file);
            if (!File.Exists(path)) return Results.NotFound();
            return await stems.SegmentAsync(path, track, ct) is { } bytes ? Results.Bytes(bytes, "video/mp2t") : Results.NotFound();
        });

        api.MapGet("/sessions/{id}/seg/{file}", (string id, string file, SessionCatalog cat, RecordingCoordinator rec) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null || file.Contains('/') || file.Contains("..")) return Results.NotFound();
            var mediaDir = dir == rec.ActiveSessionDir ? rec.ActiveSession!.MediaDir : SessionStore.Load(dir).MediaDir;
            var path = Path.Combine(mediaDir, file);
            return File.Exists(path) ? Results.File(path, "video/mp2t", enableRangeProcessing: true) : Results.NotFound();
        });

        // A small preview of the moment t seconds into the session, for hovering over the timeline.
        // Whole seconds: it is the keyframe there. 404 where the footage has gone or is not written yet.
        api.MapGet("/sessions/{id}/thumb", async (string id, double t, SessionCatalog cat, RecordingCoordinator rec, ThumbService thumbs, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null || t < 0) return Results.NotFound();
            var live = dir == rec.ActiveSessionDir;
            var index = live ? rec.ActiveSegments : new SegmentIndex(dir, SessionStore.Load(dir).MediaDir);
            var seg = index?.Available(includeTail: false).FirstOrDefault(s => t >= s.Start && t < s.Start + s.Duration);
            if (index is null || seg is null) return Results.NotFound();
            var path = await thumbs.FrameAsync(Path.Combine(index.MediaDir, seg.File), id, (int)Math.Floor(t - seg.Start), ct);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // A screenshot of the frame on screen t seconds into the session (fractional: the exact frame).
        api.MapPost("/sessions/{id}/screenshot", async (string id, double t, SessionCatalog cat, RecordingCoordinator rec, ScreenshotService shots, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null || t < 0) return Results.NotFound();
            var live = dir == rec.ActiveSessionDir;
            var store = live ? rec.ActiveSession : SessionStore.Load(dir);
            var index = live ? rec.ActiveSegments : new SegmentIndex(dir, store?.MediaDir);
            var seg = index?.Available(includeTail: false).FirstOrDefault(s => t >= s.Start && t < s.Start + s.Duration);
            if (store is null || index is null || seg is null) return Results.Json(new { ok = false, error = "that moment is not on disk (yet)" });
            var info = store.Info;
            var r = await shots.TakeAsync(Path.Combine(index.MediaDir, seg.File), t - seg.Start, info.Profile, info.TimeAt(t), shots.SteamAppId(info.Profile, info.SteamAppId), info.Id, ct);
            return Results.Json(new { ok = r.Ok, r.File, r.Path, steam = r.SteamFile is not null, r.Error });
        });

        // Removes a finished session: its folder, markers and any RAM footage. Its clips stay unless clips=true,
        // and starred clips stay even then.
        api.MapDelete("/sessions/{id}", (string id, bool? clips, SessionCatalog cat, RecordingCoordinator rec, EventLog events) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null || !File.Exists(Path.Combine(dir, "session.json"))) return Results.NotFound();
            if (dir == rec.ActiveSessionDir) return Results.Conflict(new { Error = "that session is still recording; stop it first" });
            var info = SessionStore.Load(dir).Info;
            var removed = 0;
            if (clips == true)
                foreach (var c in cat.Clips().Where(c => c.Info.SessionId == id && !c.Info.Starred))
                { File.Delete(c.Path); if (File.Exists(c.Path + ".json")) File.Delete(c.Path + ".json"); removed++; }
            try { if (info.MediaDir is not null && Directory.Exists(info.MediaDir)) Directory.Delete(info.MediaDir, true); } catch (IOException) { }
            Directory.Delete(dir, true);
            events.Write("purge", "session deleted by request" + (removed > 0 ? $" with {removed} clip{(removed == 1 ? "" : "s")}" : ""), info.Profile, id);
            return Results.Ok(new { Ok = true, ClipsDeleted = removed });
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

        api.MapPost("/sessions/{id}/export", async (string id, ExportRequest req, SessionCatalog cat, RecordingCoordinator rec, ZamaRecConfig cfg, ILogger<Program> log, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var r = await ExportRangeAsync(dir, req.Start, req.End, req.Label, req.Exact, "export", cat, rec, cfg, log, ct, req.Mix, req.MaxMb);
            return r.Ok ? Results.Ok(r) : Results.BadRequest(r);
        });

        api.MapPost("/sessions/{id}/export-around", async (string id, AroundRequest req, SessionCatalog cat, RecordingCoordinator rec, ZamaRecConfig cfg, ILogger<Program> log, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var store = dir == rec.ActiveSessionDir ? rec.ActiveSession! : SessionStore.Load(dir);
            var m = store.Markers.FirstOrDefault(x => x.Id == req.MarkerId);
            if (m?.TVideo is not double t) return Results.NotFound();
            var r = await ExportRangeAsync(dir, Math.Max(0, t - req.Before), t + req.After, req.Label ?? m.Label ?? m.Type, req.Exact, "export", cat, rec, cfg, log, ct, req.Mix);
            return r.Ok ? Results.Ok(r) : Results.BadRequest(r);
        });

        api.MapPost("/sessions/{id}/export-batch", async (string id, BatchRequest req, SessionCatalog cat, RecordingCoordinator rec, ZamaRecConfig cfg, ILogger<Program> log, CancellationToken ct) =>
        {
            var dir = cat.SessionDir(id);
            if (dir is null) return Results.NotFound();
            var store = dir == rec.ActiveSessionDir ? rec.ActiveSession! : SessionStore.Load(dir);
            var results = new List<object>();
            foreach (var m in store.Markers.Where(x => x.Type == req.Type && x.TVideo is not null).ToList())
            {
                var t = m.TVideo!.Value;
                var r = await ExportRangeAsync(dir, Math.Max(0, t - req.Before), t + req.After, m.Label ?? m.Type, req.Exact, "export", cat, rec, cfg, log, ct, req.Mix);
                results.Add(new { Marker = m.Id, r.Ok, r.Path, r.Error });
            }
            return Results.Ok(results);
        });

        // ------------------------------------------------------------ clips

        api.MapGet("/clips", (SessionCatalog cat, ZamaRecConfig cfg) =>
            Results.Ok(cat.Clips().Select(c => new
            {
                c.Profile, c.File, c.Bytes, c.Info.Label, c.Info.Source, c.Info.SessionId, c.Info.SessionOffset, c.Info.DurationSeconds,
                c.Info.CreatedAt, RecordedAt = c.At, c.Info.AudioTracks, c.Info.MixLevels, c.Info.Starred, c.Info.ExpiresAt, Markers = c.Info.Markers,
                Game = GamePresets.ResolveByProfileName(cfg, c.Profile)?.Id,
            })));

        api.MapGet("/clips/{profile}/{file}", (string profile, string file, SessionCatalog cat) =>
        {
            var p = cat.ClipPath(profile, file);
            return p is null ? Results.NotFound() : Results.File(p, "video/mp4", enableRangeProcessing: true);
        });

        api.MapGet("/clips/{profile}/{file}/thumb", async (string profile, string file, double t, SessionCatalog cat, ThumbService thumbs, CancellationToken ct) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null || t < 0) return Results.NotFound();
            var path = await thumbs.FrameAsync(p, profile, (int)Math.Floor(t), ct);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // A screenshot of the frame on screen t seconds into the clip.
        api.MapPost("/clips/{profile}/{file}/screenshot", async (string profile, string file, double t, SessionCatalog cat, ScreenshotService shots, CancellationToken ct) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null || t < 0) return Results.NotFound();
            var info = ReadClipInfo(p, profile);
            // the session's clock when it is still around, else back from the end of the clip (recorded_at is its last frame)
            var session = info.SessionId is { } sid && cat.SessionDir(sid) is { } sdir ? SessionStore.Load(sdir).Info : null;
            var at = session is not null && info.SessionOffset is double so ? session.TimeAt(so + t)
                : info.RecordedAt is { } end && info.DurationSeconds is double d ? end.AddSeconds(t - d)
                : info.RecordedAt ?? info.CreatedAt;
            var r = await shots.TakeAsync(p, t, profile, at, shots.SteamAppId(profile, session?.SteamAppId), info.SessionId, ct);
            return Results.Json(new { ok = r.Ok, r.File, r.Path, steam = r.SteamFile is not null, r.Error });
        });

        // One audio track of a clip on its own, for the mixer. Seekable, like the clip.
        api.MapGet("/clips/{profile}/{file}/audio/{track:int}", async (string profile, string file, int track, SessionCatalog cat, StemService stems, CancellationToken ct) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null || track is < 0 or > 15) return Results.NotFound();
            return await stems.ClipAsync(p, profile, track, ct) is { } path ? Results.File(path, "audio/mp4", enableRangeProcessing: true) : Results.NotFound();
        });

        // Toggles, or with ?on=true|false sets (for starring several at once).
        api.MapPost("/clips/{profile}/{file}/star", (string profile, string file, bool? on, SessionCatalog cat) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var info = ReadClipInfo(p, profile);
            info.Starred = on ?? !info.Starred;
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

        api.MapPost("/clips/{profile}/{file}/export", async (string profile, string file, ExportRequest req, SessionCatalog cat, ZamaRecConfig cfg, RecordingCoordinator rec, ILogger<Program> log, CancellationToken ct) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var src = ReadClipInfo(p, profile);
            // The new clip is dated by its footage, not by when it was cut: the source's end, less what was left off.
            var recordedAt = cat.FootageTime(src).AddSeconds(src.DurationSeconds is double len ? Math.Min(0, req.End - len) : 0);
            var target = ClipNaming.NewPath(cfg.ClipsDir, profile, req.Label ?? src.Label, at: recordedAt);
            var share = ShareBytes(req.MaxMb);
            var r = await ClipExporter.ExportFromFileAsync(p, req.Start, req.End, target, req.Exact, log, ct, src.AudioTracks, req.Mix, share);
            if (!r.Ok) return Results.BadRequest(new { r.Ok, r.Error });
            var info = new ClipInfo
            {
                Profile = profile, Label = req.Label ?? src.Label, Source = share is null ? "export" : "share", SessionId = src.SessionId,
                SessionOffset = src.SessionOffset is double o ? o + req.Start : null, DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now, RecordedAt = recordedAt,
                AudioTracks = share is null ? src.AudioTracks : null,
                MixLevels = ClipExporter.Mixes(src.AudioTracks, req.Mix) ? req.Mix : null,
                Markers = src.Markers.Where(m => m.T >= req.Start && m.T <= req.End).Select(m => new ClipMarker { T = m.T - req.Start, Type = m.Type, Label = m.Label, Data = m.Data }).ToList(),
            };
            SessionCatalog.SaveClipInfo(target, info);
            rec.RecordExport(profile, src.SessionId, target, r.Duration);
            return Results.Ok(new { r.Ok, Path = target, File = Path.GetFileName(target), r.Duration, Bytes = new FileInfo(target).Length });
        });

        // ------------------------------------------------------------ managing and sharing clips

        api.MapMethods("/clips/{profile}/{file}", ["PATCH"], (string profile, string file, LabelRequest req, SessionCatalog cat) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var info = ReadClipInfo(p, profile);
            info.Label = string.IsNullOrWhiteSpace(req.Label) ? null : req.Label.Trim();   // the file keeps its name; sessions refer to it
            SessionCatalog.SaveClipInfo(p, info);
            return Results.Ok(new { info.Label });
        });

        // Shows the clip in the file manager, selected. Opens a window, so only ever on a click.
        api.MapPost("/clips/{profile}/{file}/reveal", (string profile, string file, SessionCatalog cat) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var shown = Core.Platform.IsWindows
                ? Run("explorer.exe", ["/select,", p], wait: false)
                : Run("busctl", ["--user", "call", "org.freedesktop.FileManager1", "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1", "ShowItems", "ass", "1", new Uri(p).AbsoluteUri, ""])
                  || Run("xdg-open", [Path.GetDirectoryName(p)!]);
            return shown ? Results.Ok(new { Ok = true }) : Results.BadRequest(new { Error = "no file manager answered" });
        });

        // Sends the clip to a share target: "discord" (the webhook in [share]) or a module from [[share.targets]].
        // It runs in the background; the answer is a job to ask about.
        api.MapPost("/clips/{profile}/{file}/share/{target}", (string profile, string file, string target, ShareRequestBody req, SessionCatalog cat, ShareJobs share) =>
        {
            var p = cat.ClipPath(profile, file);
            if (p is null) return Results.NotFound();
            var (job, error) = share.Start(target, p, profile, req.Message);
            return job is null ? Results.BadRequest(new { Error = error }) : Results.Ok(ShareJobView(job));
        });
        api.MapGet("/share/jobs/{id}", (string id, ShareJobs share) => share.Find(id) is { } job ? Results.Ok(ShareJobView(job)) : Results.NotFound());
        api.MapDelete("/share/jobs/{id}", (string id, ShareJobs share) => share.CancelJob(id) ? Results.Ok(new { Ok = true }) : Results.NotFound());

        // Several clips to one target, one after the other; the answer is a batch to ask about.
        api.MapPost("/share/batches", (ShareBatchBody req, SessionCatalog cat, ShareJobs share) =>
        {
            var clips = new List<(string, string)>();
            foreach (var c in req.Clips ?? [])
            {
                if (cat.ClipPath(c.Profile, c.File) is not { } p) return Results.BadRequest(new { Error = $"no clip {c.Profile}/{c.File}" });
                clips.Add((p, c.Profile));
            }
            var (batch, error) = share.StartBatch(req.Target ?? "", clips);
            return batch is null ? Results.BadRequest(new { Error = error }) : Results.Ok(ShareBatchView(batch));
        });
        api.MapGet("/share/batches/{id}", (string id, ShareJobs share) => share.FindBatch(id) is { } b ? Results.Ok(ShareBatchView(b)) : Results.NotFound());
        api.MapDelete("/share/batches/{id}", (string id, ShareJobs share) => share.CancelBatch(id) ? Results.Ok(new { Ok = true }) : Results.NotFound());
    }

    public sealed record ShareBatchClip(string Profile, string File);
    public sealed record ShareBatchBody(string? Target, List<ShareBatchClip>? Clips);

    private static object ShareBatchView(ShareJobs.Batch b) => new
    {
        b.Id, b.Target, b.Stage, Count = b.Items.Count,
        Current = b.Current is { } j && b.Ended is null ? ShareJobView(j) : null,
        Items = b.Items.Select(i => new { i.File, i.Profile, i.Stage, i.Url, i.Error, i.Reencoded }),
    };

    private static object ShareJobView(ShareJobs.Job j) =>
        new { j.Id, j.Target, j.Stage, j.Progress, j.Note, j.Url, j.Error, File = j.File is null ? null : Path.GetFileName(j.File), j.Bytes, j.Reencoded };

    private static bool Run(string exe, IEnumerable<string> args, bool wait = true)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (!wait) return p is not null;
            return p is not null && p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Windows: the fixed drives with their free space, for picking where recordings go. Read when Settings opens.</summary>
    private static object[] Drives()
    {
        if (!Core.Platform.IsWindows) return [];
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => (object)new { Root = d.Name, Label = d.VolumeLabel, Free = d.AvailableFreeSpace, Total = d.TotalSize }).ToArray();
        }
        catch { return []; }
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
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { return 0; }
    }

    internal static ClipInfo ReadClipInfo(string clipPath, string profile)
    {
        try
        {
            if (File.Exists(clipPath + ".json"))
                return JsonSerializer.Deserialize(File.ReadAllText(clipPath + ".json"), SessionJson.Default.ClipInfo) ?? new ClipInfo { Profile = profile };
        }
        catch { }
        return new ClipInfo { Profile = profile, CreatedAt = File.GetCreationTime(clipPath) };
    }

    public sealed record ExportResult(bool Ok, string? Path, string? File, double? Duration, string? Error, long? Bytes = null);

    private static async Task<ExportResult> ExportRangeAsync(string dir, double start, double end, string? label, bool exact, string source,
        SessionCatalog cat, RecordingCoordinator rec, ZamaRecConfig cfg, ILogger log, CancellationToken ct, Dictionary<string, double>? mix = null, double? maxMb = null)
    {
        var share = ShareBytes(maxMb);
        if (share is not null) source = "share";
        var live = dir == rec.ActiveSessionDir;
        var store = live ? rec.ActiveSession! : SessionStore.Load(dir);
        var index = live ? rec.ActiveSegments : new SegmentIndex(dir, store.MediaDir);
        if (index is null) return new ExportResult(false, null, null, null, "session has no timeline");
        index.Refresh();
        var recordedAt = store.Info.TimeAt(end);   // dated by its footage, not by when it was cut
        var target = ClipNaming.NewPath(cfg.ClipsDir, store.Info.Profile, label, at: recordedAt);
        var r = await ClipExporter.ExportAsync(dir, index, start, end, target, exact, log, ct, store.Info.AudioTracks, mix, share);
        if (!r.Ok) return new ExportResult(false, null, null, null, r.Error);
        var actualStart = end - (r.Duration ?? (end - start));
        var info = new ClipInfo
        {
            Profile = store.Info.Profile, Label = label, Source = source, SessionId = store.Info.Id, SessionOffset = actualStart,
            DurationSeconds = r.Duration, CreatedAt = DateTimeOffset.Now, RecordedAt = recordedAt,
            AudioTracks = share is null ? store.Info.AudioTracks : null,   // a share copy carries one track
            MixLevels = ClipExporter.Mixes(store.Info.AudioTracks, mix) ? mix : null,
            Markers = store.Markers.Where(m => m.TVideo is double t && t >= actualStart && t <= end)
                .Select(m => new ClipMarker { T = m.TVideo!.Value - actualStart, Type = m.Type, Label = m.Label, Data = m.Data }).ToList(),
        };
        SessionCatalog.SaveClipInfo(target, info);
        store.Info.Clips.Add(Path.GetFileName(target));
        store.Save();
        log.LogInformation("exported {start:0.0}-{end:0.0}s of {id} -> {path}", start, end, store.Info.Id, target);
        rec.RecordExport(store.Info.Profile, store.Info.Id, target, r.Duration);
        return new ExportResult(true, target, Path.GetFileName(target), r.Duration, null, new FileInfo(target).Length);
    }

    /// <summary>Megabytes as the byte cap a share copy must fit: decimal megabytes, so it is under any "N MB" limit however the site counts.</summary>
    internal static long? ShareBytes(double? maxMb) => maxMb is double mb && mb > 0 ? (long)(Math.Min(mb, 4000) * 1_000_000) : null;

    /// <summary>A re-encoded copy of a whole clip that fits under <paramref name="cap"/> bytes, filed as a clip of its own.</summary>
    internal static async Task<(string? Path, string? Error)> ShareCopyAsync(string clipPath, string profile, ClipInfo src, long cap, string suffix,
        SessionCatalog cat, ZamaRecConfig cfg, RecordingCoordinator rec, ILogger log, CancellationToken ct)
    {
        var length = src.DurationSeconds ?? await VideoProbe.DurationSecondsAsync(clipPath, ct) ?? 0;
        if (length <= 0) return (null, "could not read the clip's length");
        var label = (src.Label ?? Path.GetFileNameWithoutExtension(clipPath)) + " " + suffix;
        var target = ClipNaming.NewPath(cfg.ClipsDir, profile, label, at: cat.FootageTime(src));
        var r = await ClipExporter.ExportFromFileAsync(clipPath, 0, length, target, false, log, ct, src.AudioTracks, null, cap);
        if (!r.Ok) return (null, r.Error);
        SessionCatalog.SaveClipInfo(target, new ClipInfo
        {
            Profile = profile, Label = label, Source = "share", SessionId = src.SessionId, SessionOffset = src.SessionOffset, DurationSeconds = r.Duration,
            CreatedAt = DateTimeOffset.Now, RecordedAt = cat.FootageTime(src), Markers = src.Markers,
        });
        rec.RecordExport(profile, src.SessionId, target, r.Duration);
        return (target, null);
    }
}
