using System.Globalization;
using Kairec.Core.Config;

namespace Kairec.Core.Recording;

/// <summary>Builds the gpu-screen-recorder command line for a profile. Flags verified against 6.1.3's man page.</summary>
public static class GsrArguments
{
    public const string PlaylistName = "live.m3u8";
    public const string SegmentPattern = "seg_%06d.ts";

    public static List<string> Build(KairecConfig cfg, EffectiveProfile p, string mediaDir, string bufferOutDir, string ipcSocket)
    {
        var c = cfg.Capture;
        var args = new List<string>
        {
            "-w", c.Monitor,
            "-f", p.Fps.ToString(),
            "-k", p.Codec,
            "-bm", "cbr",
            "-q", p.BitrateKbps.ToString(),
            "-keyint", p.KeyframeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-cursor", c.Cursor ? "yes" : "no",
            "-encoder", "gpu",
            "-fallback-cpu-encoding", "no",
            "-write-first-frame-ts", "yes",
            "-ipc", ipcSocket,
        };
        // One -a per track; sources joined with | share a track. The mix comes first because players only play the first.
        foreach (var track in c.EffectiveTracks()) { args.Add("-a"); args.Add(string.Join("|", track.Sources)); }
        if (!string.IsNullOrWhiteSpace(p.Scale)) { args.Add("-s"); args.Add(p.Scale.Trim()); }

        switch (p.Mode)
        {
            case CaptureMode.Timeline:
            {
                // HLS muxer: N-second MPEG-TS segments plus a playlist. With a window, ffmpeg deletes
                // segments that fall out of the playlist; the daemon keeps its own index of what it saw.
                var listSize = p.WindowMinutes <= 0 ? 0 : Math.Max(1, p.WindowMinutes * 60 / p.SegmentSeconds);
                var flags = "program_date_time" + (listSize > 0 ? "+delete_segments" : "");
                var opts = $"hls_time={p.SegmentSeconds};hls_list_size={listSize};hls_flags={flags};hls_segment_filename={Path.Combine(mediaDir, SegmentPattern)}";
                args.AddRange(["-ffmpeg-opts", opts, "-o", Path.Combine(mediaDir, PlaylistName)]);
                break;
            }
            case CaptureMode.Buffer:
                args.AddRange(["-c", "mp4", "-r", (p.BufferMinutes * 60).ToString(), "-replay-storage", p.BufferStorage,
                               "-restart-replay-on-save", "no", "-df", "no", "-o", bufferOutDir]);
                break;
        }
        return args;
    }
}
