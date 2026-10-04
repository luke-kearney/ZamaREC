using System.Globalization;

namespace ZamaRec.Core.Recording;

/// <summary>
/// Reads what ffmpeg writes with -progress: blocks of key=value lines, each ended by a progress= line. It keeps
/// the latest block and, every so often, turns the frames since the last time into one line for the log: the
/// rate the recorder really ran at. Parsing is a few short lines four times a second; nothing is asked of the GPU.
/// </summary>
public sealed class FfmpegProgress(TimeSpan firstReport, TimeSpan every)
{
    private readonly Dictionary<string, string> _block = new(StringComparer.Ordinal);
    private long _reportedFrames;
    private TimeSpan _reportedAt, _next = firstReport;

    /// <summary>Frames encoded so far.</summary>
    public long Frames { get; private set; }
    public long DupFrames { get; private set; }
    public long DropFrames { get; private set; }

    /// <summary>
    /// Takes one line; <paramref name="elapsed"/> is the time since the recorder started. Returns a report when
    /// a block has ended and one is due.
    /// </summary>
    public string? Line(string line, TimeSpan elapsed)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line[..eq].Trim();
        _block[key] = line[(eq + 1)..].Trim();
        if (key != "progress") return null;

        Frames = Long("frame"); DupFrames = Long("dup_frames"); DropFrames = Long("drop_frames");
        if (elapsed < _next) return null;
        var seconds = (elapsed - _reportedAt).TotalSeconds;
        var report = string.Create(CultureInfo.InvariantCulture,
            $"{(Frames - _reportedFrames) / Math.Max(seconds, 0.001):0.0} fps over the last {seconds:0} s ({Frames} frames in all, {DupFrames} duplicated and {DropFrames} dropped by ffmpeg, speed {Text("speed")}, {Text("bitrate")})");
        _reportedFrames = Frames; _reportedAt = elapsed; _next = elapsed + every;
        return report;
    }

    /// <summary>The whole run in one line, for when the recorder ends.</summary>
    public string Summary(TimeSpan elapsed) => string.Create(CultureInfo.InvariantCulture,
        $"{Frames} frames in {elapsed.TotalSeconds:0} s ({Frames / Math.Max(elapsed.TotalSeconds, 0.001):0.0} fps on average), {DupFrames} duplicated and {DropFrames} dropped by ffmpeg");

    private long Long(string key) => _block.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private string Text(string key) => _block.TryGetValue(key, out var v) && v.Length > 0 ? v : "?";
}
