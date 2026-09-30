using System.Collections.Concurrent;
using System.Diagnostics;
using Kairec.Core.Config;

namespace Kairec.Daemon;

/// <summary>
/// Small preview images for hovering over the timeline. Made only when the page asks for one: ffmpeg decodes the
/// single keyframe at or before the moment (keyframes are a second apart), on one CPU thread at low priority and
/// never on the GPU, and the JPEG is kept under the RAM dir for a few hours so the same spot is free next time.
/// </summary>
public sealed class ThumbService(KairecConfig cfg, ILogger<ThumbService> log)
{
    public const int Width = 240;
    private static readonly TimeSpan Keep = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _ffmpeg = new(2, 2);   // a sweep of the mouse must not start a crowd of decoders
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private long _sweptAt;

    private string CacheDir
    {
        get
        {
            var ram = Paths.Expand(cfg.Timeline.RamDir);
            var parent = Path.GetDirectoryName(ram.TrimEnd('/'));
            return Path.Combine(parent is not null && Directory.Exists(parent) ? ram : Path.Combine(Path.GetTempPath(), "kairec"), ".thumbs");
        }
    }

    /// <summary>The frame <paramref name="seconds"/> into a media file (a timeline segment or a clip). Null if it cannot be made.</summary>
    /// <param name="group">Cache folder: the session id, or the clip's profile.</param>
    public async Task<string?> FrameAsync(string mediaPath, string group, int seconds, CancellationToken ct)
    {
        var dir = Path.Combine(CacheDir, BannerService.Slug(group));
        var target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(mediaPath)}.{seconds}.jpg");
        if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(mediaPath)) return target;

        var gate = _locks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(mediaPath)) return target;
            Sweep();                          // before the folder is made: the sweep removes empty ones
            Directory.CreateDirectory(dir);
            var tmp = target + ".tmp.jpg";
            await _ffmpeg.WaitAsync(ct);
            int code; string err;
            try
            {
                // -skip_frame nokey decodes keyframes only; -noaccurate_seek takes the one the seek lands on instead of decoding on to the exact time.
                (code, err) = await RunAsync(["-threads", "1", "-skip_frame", "nokey", "-noaccurate_seek", "-ss", seconds.ToString(), "-i", mediaPath,
                    "-frames:v", "1", "-vf", $"scale={Width}:-2", "-q:v", "5", "-f", "image2", tmp], ct);
            }
            finally { _ffmpeg.Release(); }
            if (code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                try { File.Delete(tmp); } catch { }
                log.LogDebug("no thumbnail at {s}s of {file}: {err}", seconds, Path.GetFileName(mediaPath), err.Trim());
                return null;
            }
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        finally { gate.Release(); _locks.TryRemove(target, out _); }
    }

    /// <summary>Drops thumbnails older than a few hours. Runs at most every ten minutes, when a new one is made.</summary>
    private void Sweep()
    {
        var now = Environment.TickCount64;
        if (_sweptAt != 0 && now - Interlocked.Read(ref _sweptAt) < 600_000) return;
        Interlocked.Exchange(ref _sweptAt, now);
        try
        {
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.jpg", SearchOption.AllDirectories))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > Keep) File.Delete(f);
            foreach (var d in Directory.EnumerateDirectories(CacheDir))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > Keep && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        }
        catch (Exception ex) { log.LogDebug(ex, "thumbnail sweep"); }
    }

    private static async Task<(int Code, string Err)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Kairec.Core.Recording.Ffmpeg.Exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // a game comes first
        var err = p.StandardError.ReadToEndAsync(ct);
        var outp = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await err);
    }
}
