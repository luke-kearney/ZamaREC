using System.Collections.Concurrent;
using System.Diagnostics;
using Kairec.Core.Config;

namespace Kairec.Daemon;

/// <summary>
/// One audio track of a recording on its own, for the viewer's mixer: browsers play only the first track of a
/// file, so each further one has to reach the page as a separate stream. Stream copies through ffmpeg, made
/// when the page asks and never while nothing is being watched.
///   - a timeline segment: the track remuxed to an audio-only MPEG-TS, straight from ffmpeg to the response;
///   - a clip: the track copied to an audio-only MP4 kept under the RAM dir, so the player can seek in it.
/// </summary>
public sealed class StemService(KairecConfig cfg, ILogger<StemService> log)
{
    private static readonly TimeSpan Keep = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private long _sweptAt;

    private string CacheDir
    {
        get
        {
            var ram = Paths.Expand(cfg.Timeline.RamDir);
            var parent = Path.GetDirectoryName(ram.TrimEnd('/'));
            return Path.Combine(parent is not null && Directory.Exists(parent) ? ram : Path.Combine(Path.GetTempPath(), "kairec"), ".stems");
        }
    }

    /// <summary>The track's part of one timeline segment, timestamps kept so it lines up with the video. Null if there is no such track.</summary>
    public async Task<byte[]?> SegmentAsync(string segmentPath, int track, CancellationToken ct)
    {
        var (code, bytes, err) = await RunAsync(["-copyts", "-i", segmentPath, "-map", $"0:a:{track}", "-c", "copy", "-muxdelay", "0", "-f", "mpegts", "pipe:1"], ct);
        if (code == 0 && bytes.Length > 0) return bytes;
        log.LogDebug("no audio track {track} in {file}: {err}", track, Path.GetFileName(segmentPath), err.Trim());
        return null;
    }

    /// <summary>Path of an audio-only MP4 holding one track of a clip, made on first use. Null if there is no such track.</summary>
    public async Task<string?> ClipAsync(string clipPath, string profile, int track, CancellationToken ct)
    {
        var dir = Path.Combine(CacheDir, BannerService.Slug(profile));
        var target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(clipPath)}.a{track}.mp4");
        var gate = _locks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(clipPath)) return target;
            Directory.CreateDirectory(dir);
            Sweep();
            var tmp = target + ".tmp.mp4";
            var (code, _, err) = await RunAsync(["-i", clipPath, "-map", $"0:a:{track}", "-c", "copy", "-movflags", "+faststart", tmp], ct);
            if (code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                try { File.Delete(tmp); } catch { }
                log.LogDebug("no audio track {track} in {file}: {err}", track, Path.GetFileName(clipPath), err.Trim());
                return null;
            }
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        finally { gate.Release(); }
    }

    /// <summary>Drops cached tracks nobody has asked for in a while. Runs at most every ten minutes, when a new one is made.</summary>
    private void Sweep()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _sweptAt) < 600_000 && _sweptAt != 0) return;
        Interlocked.Exchange(ref _sweptAt, now);
        try
        {
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.mp4", SearchOption.AllDirectories))
                if (DateTime.UtcNow - File.GetLastAccessTimeUtc(f) > Keep && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > Keep) File.Delete(f);
        }
        catch (Exception ex) { log.LogDebug(ex, "stem sweep"); }
    }

    private static async Task<(int Code, byte[] Out, string Err)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Kairec.Core.Recording.Ffmpeg.Exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, ms.ToArray(), await err);
    }
}
