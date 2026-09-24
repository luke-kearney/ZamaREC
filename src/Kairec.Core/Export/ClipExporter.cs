using System.Diagnostics;
using System.Globalization;
using Kairec.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Kairec.Core.Export;

/// <summary>Cuts clips out of a timeline session with ffmpeg. Stream copy by default; nvenc re-encode for frame-exact cuts.</summary>
public static class ClipExporter
{
    public sealed record Result(bool Ok, string? Path, double? Duration, string? Error);

    /// <summary>
    /// Exports [start, end] seconds of the session timeline to <paramref name="outputPath"/>.
    /// With stream copy the cut lands on the keyframe at or before <paramref name="start"/>, so the clip may
    /// begin up to one keyframe interval early; the caller can read the exact duration from the result.
    /// </summary>
    public static async Task<Result> ExportAsync(string sessionDir, SegmentIndex index, double start, double end, string outputPath, bool exact, ILogger log, CancellationToken ct)
    {
        if (end <= start) return new Result(false, null, null, "end must be after start");
        var segs = index.Available().Where(s => s.Start + s.Duration > start && s.Start < end).OrderBy(s => s.Seq).ToList();
        if (segs.Count == 0) return new Result(false, null, null, "that range is no longer in the timeline window");

        var first = segs[0];
        var seekInto = Math.Max(0, start - first.Start);
        // Stream copy must begin on a keyframe. Keyframes sit at whole keyframe intervals from each segment
        // start, so snap the seek down to the previous one (with a small margin) and lengthen the cut to match.
        // The concat demuxer cannot input-seek, so the seek is an output option.
        if (!exact) seekInto = Math.Max(0, Math.Floor(seekInto) - 0.05);
        var duration = end - (first.Start + seekInto);

        var listFile = Path.Combine(Path.GetTempPath(), $"kairec-concat-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(listFile, segs.Select(s => $"file '{Path.Combine(sessionDir, s.File).Replace("'", "'\\''")}'"), ct);
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            if (exact) args.AddRange(["-hwaccel", "cuda"]);
            args.AddRange(["-f", "concat", "-safe", "0", "-i", listFile, "-ss", F(seekInto), "-t", F(duration)]);
            if (exact) args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "21", "-b:v", "0", "-c:a", "copy"]);
            else args.AddRange(["-c", "copy"]);
            args.AddRange(["-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outputPath]);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var (code, stderr) = await RunAsync("ffmpeg", args, ct);
            if (code != 0)
            {
                log.LogError("ffmpeg failed ({code}): {err}", code, stderr);
                return new Result(false, null, null, stderr.Trim().Split('\n').LastOrDefault() ?? "ffmpeg failed");
            }
            var dur = await Recording.VideoProbe.DurationSecondsAsync(outputPath, ct);
            return new Result(true, outputPath, dur, null);
        }
        finally { try { File.Delete(listFile); } catch { } }
    }

    /// <summary>Cuts [start, end] out of a finished MP4 clip. Input seeking works here, so the cut is keyframe-accurate.</summary>
    public static async Task<Result> ExportFromFileAsync(string sourcePath, double start, double end, string outputPath, bool exact, ILogger log, CancellationToken ct)
    {
        if (end <= start) return new Result(false, null, null, "end must be after start");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (exact) args.AddRange(["-hwaccel", "cuda"]);
        args.AddRange(["-ss", F(start), "-i", sourcePath, "-t", F(end - start)]);
        if (exact) args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "21", "-b:v", "0", "-c:a", "copy"]);
        else args.AddRange(["-c", "copy"]);
        args.AddRange(["-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outputPath]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var (code, stderr) = await RunAsync("ffmpeg", args, ct);
        if (code != 0)
        {
            log.LogError("ffmpeg failed ({code}): {err}", code, stderr);
            return new Result(false, null, null, stderr.Trim().Split('\n').LastOrDefault() ?? "ffmpeg failed");
        }
        var dur = await Recording.VideoProbe.DurationSecondsAsync(outputPath, ct);
        return new Result(true, outputPath, dur, null);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<(int Code, string Stderr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        var outp = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await err);
    }
}
