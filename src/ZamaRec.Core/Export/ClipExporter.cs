using System.Diagnostics;
using System.Globalization;
using ZamaRec.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace ZamaRec.Core.Export;

/// <summary>Cuts clips out of a timeline session with ffmpeg. Stream copy by default; frame-exact cuts re-encode only the head on the GPU.</summary>
public static class ClipExporter
{
    /// <summary>The GPU vendor whose decoder and encoder exports use (capture.gpu); the daemon points this at its live config.</summary>
    public static Func<string> GpuSource { get; set; } = () => "auto";
    private static string Gpu => GpuSource();

    public sealed record Result(bool Ok, string? Path, double? Duration, string? Error);

    /// <summary>
    /// Exports [start, end] seconds of the session timeline to <paramref name="outputPath"/>.
    /// With stream copy the cut lands on the keyframe at or before <paramref name="start"/>, so the clip may
    /// begin up to one keyframe interval early; the caller can read the exact duration from the result.
    /// </summary>
    public static async Task<Result> ExportAsync(string sessionDir, SegmentIndex index, double start, double end, string outputPath, bool exact, ILogger log, CancellationToken ct, IReadOnlyList<string>? tracks = null, IReadOnlyDictionary<string, double>? mix = null, long? maxBytes = null)
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

        var listFile = Path.Combine(Path.GetTempPath(), $"zamarec-concat-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(listFile, ConcatList(segs, index.MediaDir), ct);
        try
        {
            if (maxBytes is long cap)
            {
                // A share copy is re-encoded anyway, so the cut is exact: from "start", not from the keyframe before it.
                var from = Math.Max(0, start - first.Start);
                return await ShareAsync(["-f", "concat", "-safe", "0", "-i", listFile, "-ss", F(from), "-t", F(end - start)],
                    Path.Combine(index.MediaDir, first.File), end - start, cap, outputPath, tracks, mix, log, ct);
            }
            if (exact)
            {
                // Concat time of each listed file's start, as the demuxer places them: by the durations in the list.
                var pieces = new List<SmartPiece>();
                double at = 0;
                foreach (var s in segs)
                {
                    var listed = s.Duration > 0 && s.Duration < 1e6 ? double.Parse(F(s.Duration), CultureInfo.InvariantCulture) : (double?)null;
                    pieces.Add(new SmartPiece(Path.Combine(index.MediaDir, s.File), at, listed));
                    at += listed ?? 0;
                }
                var smart = await SmartCutAsync(t => ["-f", "concat", "-safe", "0", "-i", listFile, "-ss", F6(t)], listFile, true,
                    pieces, seekInto, end - first.Start, outputPath, tracks, mix, log, ct);
                if (smart is not null) return smart;
            }
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            if (exact) args.AddRange(Recording.HwCodec.Decode(Gpu, keepOnGpu: false));
            args.AddRange(["-f", "concat", "-safe", "0", "-i", listFile, "-ss", F(seekInto), "-t", F(duration)]);
            args.AddRange(Tracks(tracks, mix));
            if (exact) args.AddRange(["-c:v", Recording.HwCodec.Encoder("h264", Gpu), .. Recording.HwCodec.ExactEncode(Gpu), "-c:a", "copy"]);
            else args.AddRange(["-c", "copy"]);
            args.AddRange(MixCodec(tracks, mix));
            args.AddRange(["-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outputPath]);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var (code, stderr) = await RunAsync(Recording.Ffmpeg.Exe, args, ct);
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

    /// <summary>
    /// The concat demuxer's list. Each segment gets its playlist duration, because the demuxer otherwise places
    /// the next file by the probed duration, and that is the span of the file's earliest to latest packet across
    /// every stream: a separate audio track that drifts late (the mic does, by 0.45 s after an hour) stretches
    /// it past the real 5 s and leaves a hole at every boundary, the "stutter every 5 seconds". The open-ended
    /// tail (still being written) gets no duration.
    /// </summary>
    public static IEnumerable<string> ConcatList(IEnumerable<SegmentEntry> segs, string mediaDir)
    {
        foreach (var s in segs)
        {
            yield return $"file '{Path.Combine(mediaDir, s.File).Replace("'", "'\\''")}'";
            if (s.Duration > 0 && s.Duration < 1e6) yield return $"duration {F(s.Duration)}";
        }
    }

    /// <summary>Cuts [start, end] out of a finished MP4 clip. Input seeking works here, so the cut is keyframe-accurate.</summary>
    public static async Task<Result> ExportFromFileAsync(string sourcePath, double start, double end, string outputPath, bool exact, ILogger log, CancellationToken ct, IReadOnlyList<string>? tracks = null, IReadOnlyDictionary<string, double>? mix = null, long? maxBytes = null)
    {
        if (end <= start) return new Result(false, null, null, "end must be after start");
        if (maxBytes is long cap)
            return await ShareAsync(["-ss", F(start), "-i", sourcePath, "-t", F(end - start)], sourcePath, end - start, cap, outputPath, tracks, mix, log, ct);
        if (exact && await SmartCutAsync(t => ["-ss", F6(t), "-i", sourcePath], sourcePath, false,
                [new SmartPiece(sourcePath, 0, null)], start, end, outputPath, tracks, mix, log, ct) is { } smart)
            return smart;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (exact) args.AddRange(Recording.HwCodec.Decode(Gpu, keepOnGpu: false));
        // A stream copy starts on the keyframe at or before "start", up to a keyframe interval early. Copied audio
        // comes along from there; a remade mix would begin only at "start" and leave that lead-in silent. So for
        // a mix, start the cut on the whole second, where the keyframe is (as ExportAsync assumes too).
        if (!exact && Mixes(tracks, mix) && Math.Floor(start) + 0.02 < start) start = Math.Floor(start) + 0.02;
        args.AddRange(["-ss", F(start), "-i", sourcePath, "-t", F(end - start)]);
        args.AddRange(Tracks(tracks, mix));
        if (exact) args.AddRange(["-c:v", Recording.HwCodec.Encoder("h264", Gpu), .. Recording.HwCodec.ExactEncode(Gpu), "-c:a", "copy"]);
        else args.AddRange(["-c", "copy"]);
        args.AddRange(MixCodec(tracks, mix));
        args.AddRange(["-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outputPath]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var (code, stderr) = await RunAsync(Recording.Ffmpeg.Exe, args, ct);
        if (code != 0)
        {
            log.LogError("ffmpeg failed ({code}): {err}", code, stderr);
            return new Result(false, null, null, stderr.Trim().Split('\n').LastOrDefault() ?? "ffmpeg failed");
        }
        var dur = await Recording.VideoProbe.DurationSecondsAsync(outputPath, ct);
        return new Result(true, outputPath, dur, null);
    }

    // ---------------------------------------------------------------- frame-exact: re-encode the head only

    /// <summary>One file of the source as the concat list places it: where it starts on the list's timeline, and its listed duration.</summary>
    private sealed record SmartPiece(string File, double At, double? Listed);

    /// <summary>
    /// A frame-exact cut that re-encodes only the head: the frames from the In point up to the next keyframe
    /// (under a second at the recorder's 1 s keyframes), joined to a stream copy of the rest. The recorder's
    /// H.264 has no B-frames and an IDR with its SPS and PPS at every keyframe, so the copy can start there and
    /// stop on any frame; the head is encoded with the same profile and no B-frames, and NVENC then writes the
    /// recorder's SPS and PPS (only the HRD rate differs), so the join plays like one stream.
    /// <paramref name="start"/> and <paramref name="end"/> are on the list's timeline; <paramref name="seekInput"/>
    /// opens the source at a time on it. Null when the source is not like that (another codec, B-frames, no
    /// keyframe inside the range) or a step fails: the caller then re-encodes the whole range.
    /// </summary>
    private static async Task<Result?> SmartCutAsync(Func<double, List<string>> seekInput, string probeTarget, bool concat, List<SmartPiece> pieces,
        double start, double end, string outputPath, IReadOnlyList<string>? tracks, IReadOnlyDictionary<string, double>? mix, ILogger log, CancellationToken ct)
    {
        const double Eps = 0.00005;   // well under a frame, well over the rounding of the times ffprobe prints
        var info = Fields(await ProbeAsync(["-v", "error", "-show_entries", "stream=codec_type,codec_name,profile,has_b_frames:format=start_time", "-of", "default=nw=1", pieces[0].File], ct));
        var profile = info.GetValueOrDefault("profile");
        if (info.GetValueOrDefault("codec_name") != "h264" || info.GetValueOrDefault("has_b_frames") != "0" || profile is not ("High" or "Main"))
        {
            log.LogInformation("frame-exact export: the source is {codec} {profile} with b-frames {b}, so the whole range is re-encoded",
                info.GetValueOrDefault("codec_name"), profile, info.GetValueOrDefault("has_b_frames"));
            return null;
        }
        var hasAudio = info.ContainsKey("codec_type=audio");
        static double StartTime(Dictionary<string, string> f) => double.TryParse(f.GetValueOrDefault("start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var st0 = StartTime(info);

        // The video frames around the In point, in stream order, on the list's timeline: the first at or after the In point is where the clip
        // starts, the first keyframe after it in the stream is where the copy can. The head is counted in frames, not cut by time: a segment's
        // last frames can sit a fraction of a millisecond past where the list places the next one, and a cut by time would lose them.
        var probe = new List<string> { "-v", "error" };
        if (concat) probe.AddRange(["-f", "concat", "-safe", "0", "-i", probeTarget, "-read_intervals", $"%+{F6(start + 2.5)}"]);
        else probe.AddRange(["-read_intervals", $"{F6(Math.Max(0, st0 + start - 1))}%+3.5", "-i", probeTarget]);
        probe.AddRange(["-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0"]);
        var frames = (await ProbeAsync(probe, ct)).Split('\n').Select(l => l.Split(','))
            .Where(p => p.Length >= 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(p => (T: double.Parse(p[0], CultureInfo.InvariantCulture) - (concat ? 0 : st0), Key: p[1].StartsWith('K')))
            .ToList();
        var firstAt = frames.FindIndex(p => p.T >= start - Eps * 10);
        var keyAt = firstAt < 0 ? -1 : frames.FindIndex(firstAt, p => p.Key);
        var (first, key) = (firstAt < 0 ? default : frames[firstAt], keyAt < 0 ? default : frames[keyAt]);
        if (keyAt < 0 || key.T >= end - 0.001)
        {
            log.LogInformation("frame-exact export: no keyframe between {start:0.###} and {end:0.###}, so the whole range is re-encoded", start, end);
            return null;
        }
        var head = key.T - first.T;
        var headFrames = keyAt - firstAt;

        // The file the keyframe is in, and its time there: inpoint is read in the file's own timestamps.
        var j = pieces.FindLastIndex(p => p.At <= key.T + 1e-6);
        var pj = pieces[j];
        var stj = j == 0 ? st0 : StartTime(Fields(await ProbeAsync(["-v", "error", "-show_entries", "format=start_time", "-of", "default=nw=1", pj.File], ct)));
        var inpoint = key.T - pj.At + stj;

        var work = outputPath + ".parts";
        Directory.CreateDirectory(work);
        try
        {
            var list = new List<string>();
            if (headFrames > 0)
            {
                var headFile = Path.Combine(work, "head.ts");
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
                args.AddRange(Recording.HwCodec.Decode(Gpu, keepOnGpu: false));
                args.AddRange(seekInput(first.T - Eps));
                args.AddRange(["-frames:v", headFrames.ToString(CultureInfo.InvariantCulture), "-map", "0:v:0", "-an", "-sn", "-fps_mode", "passthrough",
                    "-c:v", Recording.HwCodec.Encoder("h264", Gpu), .. Recording.HwCodec.ExactEncode(Gpu), "-bf", "0", "-profile:v", profile.ToLowerInvariant(),
                    "-f", "mpegts", headFile]);
                var (code, err) = await RunAsync(Recording.Ffmpeg.Exe, args, ct);
                if (code != 0) { log.LogWarning("frame-exact export: the head encode failed ({err}), so the whole range is re-encoded", err.Trim()); return null; }
                list.AddRange([Entry(headFile), $"duration {F6(head)}"]);
            }
            list.AddRange([Entry(pj.File), $"inpoint {F6(inpoint + Eps)}"]);
            if (pj.Listed is double lj) list.Add($"duration {F6(lj - (key.T - pj.At))}");
            foreach (var p in pieces.Skip(j + 1))
            {
                list.Add(Entry(p.File));
                if (p.Listed is double l) list.Add($"duration {F6(l)}");
            }
            var videoList = Path.Combine(work, "video.txt");
            await File.WriteAllLinesAsync(videoList, list, ct);

            // The audio of the same range, copied on its own: the concat input cannot seek, and a seek on the output would cut the video too.
            var audioFile = Path.Combine(work, "audio.mka");
            if (hasAudio)
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
                args.AddRange(seekInput(first.T - Eps));
                args.AddRange(["-t", F6(end - first.T - Eps * 10), "-map", "0:a", "-vn", "-c", "copy", audioFile]);
                var (code, err) = await RunAsync(Recording.Ffmpeg.Exe, args, ct);
                if (code != 0) { log.LogWarning("frame-exact export: copying the audio failed ({err}), so the whole range is re-encoded", err.Trim()); return null; }
            }

            var mux = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", videoList };
            if (hasAudio) mux.AddRange(["-i", audioFile]);
            mux.AddRange(["-t", F6(end - first.T - Eps * 10)]);
            mux.AddRange(hasAudio ? (Mixes(tracks, mix) ? MixedTracks(tracks!, mix!, audioInput: 1) : AllTracks(tracks, audioInput: 1)) : ["-map", "0:v:0"]);
            mux.AddRange(["-c", "copy", .. MixCodec(tracks, mix), "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outputPath]);
            var (mcode, merr) = await RunAsync(Recording.Ffmpeg.Exe, mux, ct);
            if (mcode != 0) { log.LogWarning("frame-exact export: joining failed ({err}), so the whole range is re-encoded", merr.Trim()); return null; }
            if (headFrames > 0) log.LogInformation("frame-exact export: re-encoded the first {frames} frames ({head:0.000} s), copied the rest", headFrames, head);
            else log.LogInformation("frame-exact export: starts on a keyframe, copied it all");
            return new Result(true, outputPath, await Recording.VideoProbe.DurationSecondsAsync(outputPath, ct), null);
        }
        finally { try { Directory.Delete(work, recursive: true); } catch { } }

        static string Entry(string file) => $"file '{file.Replace("'", "'\\''")}'";
    }

    /// <summary>ffprobe's key=value lines as a dictionary; a key that repeats (one per stream) keeps its first value, and each codec_type seen is recorded as "codec_type=X".</summary>
    private static Dictionary<string, string> Fields(string output)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var (k, v) = (line[..i].Trim(), line[(i + 1)..].Trim());
            if (k == "codec_type") d[$"codec_type={v}"] = "1";
            d.TryAdd(k, v);
        }
        return d;
    }

    private static async Task<string> ProbeAsync(IEnumerable<string> args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(Recording.Ffmpeg.Probe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(ct);
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            await err;
            return output;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return ""; }
    }

    /// <summary>
    /// ffmpeg keeps one audio stream unless told otherwise: map the video and every audio track, and title the
    /// tracks so players and editors show "mix", "game", "mic" rather than "Track 2". The audio can come from another input.
    /// </summary>
    public static List<string> AllTracks(IReadOnlyList<string>? tracks, int audioInput = 0)
    {
        var args = new List<string> { "-map", "0:v:0", "-map", $"{audioInput}:a?" };
        // MP4 has two places a track name can live; players differ in which they read, so both are set.
        for (int i = 0; tracks is not null && i < tracks.Count; i++)
            args.AddRange([$"-metadata:s:a:{i}", "title=" + tracks[i], $"-metadata:s:a:{i}", "handler_name=" + tracks[i]]);
        return args;
    }

    // ---------------------------------------------------------------- share size: a copy that fits under a cap

    /// <summary>How a clip is encoded to fit a size: what is left for the video after the audio, and the picture that bitrate can carry.</summary>
    public sealed record SharePlan(int VideoKbps, int AudioKbps, int Height, int Fps);

    /// <summary>
    /// Picks bitrates and a picture size for <paramref name="seconds"/> of video to land under <paramref name="maxBytes"/>.
    /// The whole budget (less 6% for the container and the encoder's overshoot) is spread over the length; the
    /// picture steps down a ladder until each pixel gets enough bits to look decent, 60 fps kept as long as it can be.
    /// Null when even the smallest step would be mush: the clip is too long for that size.
    /// </summary>
    public static SharePlan? PlanShare(long maxBytes, double seconds, int srcWidth, int srcHeight, double srcFps)
    {
        if (seconds <= 0 || srcHeight <= 0) return null;
        var total = maxBytes * 8 * 0.94 / seconds / 1000;
        var audio = total >= 1200 ? 128 : total >= 600 ? 96 : 64;
        var video = (int)(total - audio);
        if (video < 250) return null;
        var fps = srcFps > 45 ? 60 : 30;
        (int H, int Fps)[] ladder = [(srcHeight, fps), (1080, 60), (720, 60), (720, 30), (540, 30), (480, 30), (360, 30)];
        const double bitsPerPixel = 0.055;
        foreach (var (h, f) in ladder)
        {
            if (h > srcHeight || f > fps) continue;
            var w = (double)srcWidth * h / srcHeight;
            if (video >= w * h * f * bitsPerPixel / 1000) return new SharePlan(video, audio, h, f);
        }
        return new SharePlan(video, audio, Math.Min(srcHeight, 360), 30);
    }

    /// <summary>The ffmpeg arguments after the input for a share-size encode: NVENC at the planned bitrate, scaled on the GPU, one stereo audio track.</summary>
    private static List<string> ShareArgs(SharePlan plan, int srcHeight, IReadOnlyList<string>? tracks, IReadOnlyDictionary<string, double>? mix, double scale = 1)
    {
        var v = Math.Max(150, (int)(plan.VideoKbps * scale));
        var args = new List<string>();
        if (Mixes(tracks, mix))
        {
            var mixed = MixedTracks(tracks!, mix!);
            args.AddRange(["-filter_complex", mixed[1], "-map", "0:v:0", "-map", "[mix]"]);   // only the new mix: a shared clip carries one track
        }
        else args.AddRange(["-map", "0:v:0", "-map", "0:a:0?"]);
        if (plan.Height < srcHeight) args.AddRange(["-vf", Recording.HwCodec.Scale(Gpu, plan.Height)]);
        args.AddRange(["-r", plan.Fps.ToString(), "-c:v", Recording.HwCodec.Encoder("h264", Gpu), .. Recording.HwCodec.ShareEncode(Gpu, v),
            "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k", "-ac", "2", "-movflags", "+faststart"]);
        return args;
    }

    /// <summary>
    /// Runs a share-size encode and checks the result really is under the cap; if the encoder overshot, once more
    /// with the bitrate scaled down by as much. <paramref name="input"/> is everything up to and including the cut
    /// (-ss/-i/-t), decoded on the GPU so the frames never leave it.
    /// </summary>
    private static async Task<Result> ShareAsync(List<string> input, string probePath, double seconds, long maxBytes, string outputPath,
        IReadOnlyList<string>? tracks, IReadOnlyDictionary<string, double>? mix, ILogger log, CancellationToken ct)
    {
        var src = await Recording.VideoProbe.InfoAsync(probePath, ct);
        if (src is null) return new Result(false, null, null, "could not read the source video");
        var plan = PlanShare(maxBytes, seconds, src.Width, src.Height, src.Fps);
        if (plan is null)
            return new Result(false, null, null, $"{seconds:0} s is too long for {maxBytes / 1e6:0.#} MB: shorten the range or pick a larger size");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        double scale = 1;
        for (int attempt = 0; ; attempt++)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            args.AddRange(Recording.HwCodec.Decode(Gpu, keepOnGpu: true));
            args.AddRange(input);
            args.AddRange(ShareArgs(plan, src.Height, tracks, mix, scale));
            args.Add(outputPath);
            var (code, stderr) = await RunAsync(Recording.Ffmpeg.Exe, args, ct);
            if (code != 0)
            {
                log.LogError("ffmpeg (share) failed ({code}): {err}", code, stderr);
                return new Result(false, null, null, stderr.Trim().Split('\n').LastOrDefault() ?? "ffmpeg failed");
            }
            var size = new FileInfo(outputPath).Length;
            log.LogInformation("share encode: {h}p{fps} at {v} kbps -> {mb:0.00} MB (cap {cap:0.#} MB)", plan.Height, plan.Fps, (int)(plan.VideoKbps * scale), size / 1e6, maxBytes / 1e6);
            if (size <= maxBytes || attempt >= 2) break;
            scale *= (double)maxBytes / size * 0.96;
        }
        if (new FileInfo(outputPath).Length > maxBytes)
        {
            try { File.Delete(outputPath); } catch { }
            return new Result(false, null, null, "could not get under that size; pick a larger one");
        }
        return new Result(true, outputPath, await Recording.VideoProbe.DurationSecondsAsync(outputPath, ct), null);
    }

    /// <summary>True when the recording has separate tracks to mix and levels were given for them.</summary>
    public static bool Mixes(IReadOnlyList<string>? tracks, IReadOnlyDictionary<string, double>? mix) => mix is not null && tracks is { Count: >= 2 };

    private static List<string> Tracks(IReadOnlyList<string>? tracks, IReadOnlyDictionary<string, double>? mix) =>
        Mixes(tracks, mix) ? MixedTracks(tracks!, mix!) : AllTracks(tracks);

    /// <summary>
    /// Replaces the recorded mix with a new one: every separate track at its level, summed, with a limiter so a
    /// boosted track cannot clip. The new mix is the first track, which is what players play; the separate
    /// tracks follow it untouched. A track without a level counts as 1.
    /// </summary>
    public static List<string> MixedTracks(IReadOnlyList<string> tracks, IReadOnlyDictionary<string, double> mix, int audioInput = 0)
    {
        var stems = Enumerable.Range(1, tracks.Count - 1).ToList();
        double Level(int i) => Math.Clamp(mix.TryGetValue(tracks[i], out var g) ? g : 1, 0, 4);
        var graph = string.Join(";", stems.Select(i => $"[{audioInput}:a:{i}]volume={F(Level(i))}[m{i}]")) + ";"
            + string.Concat(stems.Select(i => $"[m{i}]")) + $"amix=inputs={stems.Count}:normalize=0,alimiter=limit=0.97[mix]";
        var args = new List<string> { "-filter_complex", graph, "-map", "0:v:0", "-map", "[mix]" };
        foreach (var i in stems) args.AddRange(["-map", $"{audioInput}:a:{i}"]);
        for (int i = 0; i < tracks.Count; i++)
            args.AddRange([$"-metadata:s:a:{i}", "title=" + tracks[i], $"-metadata:s:a:{i}", "handler_name=" + tracks[i]]);
        return args;
    }

    // The new mix is the only stream that is encoded; it comes after "-c copy" so it wins for that one stream.
    private static string[] MixCodec(IReadOnlyList<string>? tracks, IReadOnlyDictionary<string, double>? mix) =>
        Mixes(tracks, mix) ? ["-c:a:0", "aac", "-b:a:0", "192k"] : [];

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string F6(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static async Task<(int Code, string Stderr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        var outp = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await err);
    }
}
