using System.Globalization;
using System.Text.Json;
using ZamaRec.Core.Config;
using ZamaRec.Core.Processes;
using ZamaRec.Core.Sessions;

namespace ZamaRec.Daemon;

/// <summary>
/// A still from a recording, taken in the player: the frame on screen at that moment, as a PNG under
/// screenshots/&lt;profile&gt;/ with a .json beside it. For a Steam game a JPEG copy also goes into Steam's own
/// screenshots of the game ([screenshots].steam), where Steam finds it when it next starts and from where it can be
/// uploaded to the profile. Made only on a click, on the CPU at low priority, like the hover thumbnails.
/// </summary>
public sealed class ScreenshotService(ZamaRecConfig cfg, ILogger<ScreenshotService> log)
{
    public sealed record Result(bool Ok, string? Path, string? File, string? SteamFile, string? Error);

    /// <param name="mediaPath">A timeline segment or a clip.</param>
    /// <param name="offset">Seconds into that file of the frame on screen.</param>
    /// <param name="recordedAt">When that frame was recorded: the name and date of both copies.</param>
    public async Task<Result> TakeAsync(string mediaPath, double offset, string profile, DateTimeOffset recordedAt, long? steamAppId, string? sessionId, CancellationToken ct)
    {
        var target = ClipNaming.NewPath(cfg.ScreenshotsDir, profile, null, ".png", recordedAt);
        var tmp = target + ".tmp.png";
        // The frame on screen is the last one that starts at or before the moment. Decode from a little earlier and
        // keep overwriting the output with each frame up to the moment: the last one written is that frame.
        var back = Math.Min(offset, 1.0);
        var inv = CultureInfo.InvariantCulture;
        var (code, err) = await ThumbService.RunAsync(["-ss", (offset - back).ToString("0.######", inv), "-i", mediaPath,
            "-vf", $"select=lte(t\\,{(back + 0.0005).ToString("0.######", inv)})", "-fps_mode", "vfr", "-update", "1", "-f", "image2", tmp], ct);
        if (code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
        {
            try { File.Delete(tmp); } catch { }
            log.LogWarning("no screenshot at {s:0.###}s of {file}: {err}", offset, Path.GetFileName(mediaPath), err.Trim());
            return new Result(false, null, null, null, "could not take the frame from the recording");
        }
        File.Move(tmp, target);

        string? steamFile = null;
        if (cfg.Screenshots.Steam && steamAppId is long app && SteamLibrary.ScreenshotDir(app) is { } steamDir)
        {
            try { steamFile = await ToSteamAsync(target, steamDir, recordedAt, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "screenshot not copied to Steam ({dir})", steamDir); }
        }

        try
        {
            File.WriteAllText(target + ".json", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["profile"] = profile, ["session_id"] = sessionId, ["recorded_at"] = recordedAt,
                ["steam_app_id"] = steamAppId, ["steam_file"] = steamFile,
            }));
        }
        catch (IOException ex) { log.LogDebug(ex, "screenshot sidecar"); }
        log.LogInformation("screenshot {file}{steam}", Path.GetFileName(target), steamFile is null ? "" : $", in Steam as {Path.GetFileName(steamFile)}");
        return new Result(true, target, Path.GetFileName(target), steamFile, null);
    }

    /// <summary>
    /// A JPEG and its 200 px thumbnail in Steam's folder for the game, named and dated as Steam names its own.
    /// Steam reads the date from the file, so both carry the moment of the frame.
    /// </summary>
    private static async Task<string?> ToSteamAsync(string png, string steamDir, DateTimeOffset recordedAt, CancellationToken ct)
    {
        var thumbs = Path.Combine(steamDir, "thumbnails");
        Directory.CreateDirectory(thumbs);
        var stamp = recordedAt.LocalDateTime.ToString("yyyyMMddHHmmss");
        var name = $"{stamp}_1.jpg";
        for (int i = 2; File.Exists(Path.Combine(steamDir, name)); i++) name = $"{stamp}_{i}.jpg";
        var jpg = Path.Combine(steamDir, name);
        var thumb = Path.Combine(thumbs, name);
        var (c1, e1) = await ThumbService.RunAsync(["-i", png, "-q:v", "2", "-f", "image2", jpg], ct);
        var (c2, e2) = await ThumbService.RunAsync(["-i", png, "-vf", "scale=200:-2", "-q:v", "3", "-f", "image2", thumb], ct);
        if (c1 != 0 || c2 != 0)
        {
            try { File.Delete(jpg); File.Delete(thumb); } catch { }
            throw new IOException((e1 + e2).Trim());
        }
        File.SetLastWriteTime(jpg, recordedAt.LocalDateTime);
        File.SetLastWriteTime(thumb, recordedAt.LocalDateTime);
        return jpg;
    }

    /// <summary>The game's Steam app id: the session's own, else the profile's or its game preset's.</summary>
    public long? SteamAppId(string profile, long? sessionAppId) =>
        sessionAppId ?? cfg.FindProfile(profile)?.SteamAppId ?? GamePresets.ResolveByProfileName(cfg, profile)?.SteamAppId;
}
