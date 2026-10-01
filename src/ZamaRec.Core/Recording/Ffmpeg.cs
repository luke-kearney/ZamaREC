using System.Diagnostics;

namespace ZamaRec.Core.Recording;

/// <summary>
/// Where ffmpeg and ffprobe are. ZAMAREC_FFMPEG_DIR wins; then the daemon's own folder (the Windows bundle ships
/// them next to the exe, also under ffmpeg\bin); then PATH, which is how the Linux install finds them.
/// </summary>
public static class Ffmpeg
{
    private static string? _exe, _probe;

    public static string Exe => _exe ??= Find("ffmpeg");
    public static string Probe => _probe ??= Find("ffprobe");

    private static string Find(string name)
    {
        var file = Platform.ExeName(name);
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("ZAMAREC_FFMPEG_DIR") is { Length: > 0 } env) dirs.Add(env);
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"));
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, file);
            if (File.Exists(p)) return p;
        }
        return name;   // on PATH, or not at all: the error then names it
    }

    /// <summary>True when ffmpeg can be started at all; used for the startup check on Windows.</summary>
    public static bool Available()
    {
        try
        {
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// The GPU encoder and decoder names ffmpeg uses for the GPU vendor (configured, or detected). NVIDIA is the tested path on
/// both systems; AMD and Intel are wired the way ffmpeg documents them and have not been tried on real hardware.
/// </summary>
public static class HwCodec
{
    /// <summary>capture.gpu as the vendor whose codecs to use: "auto", or a vendor this machine lacks, becomes the detected one.</summary>
    public static string Normalize(string? gpu) => GpuVendor.Resolve(gpu, GpuVendor.Detected);

    public static string Encoder(string codec, string gpu) => (codec.ToLowerInvariant(), Normalize(gpu)) switch
    {
        ("hevc", "amd") => "hevc_amf", ("hevc", "intel") => "hevc_qsv", ("hevc", _) => "hevc_nvenc",
        ("av1", "amd") => "av1_amf", ("av1", "intel") => "av1_qsv", ("av1", _) => "av1_nvenc",
        (_, "amd") => "h264_amf", (_, "intel") => "h264_qsv", _ => "h264_nvenc",
    };

    /// <summary>Input-side arguments to decode on the GPU. With <paramref name="keepOnGpu"/> the frames stay there for scaling (NVIDIA only).</summary>
    public static string[] Decode(string gpu, bool keepOnGpu)
    {
        var g = Normalize(gpu);
        if (g == "nvidia") return keepOnGpu ? ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"] : ["-hwaccel", "cuda"];
        return Platform.IsWindows ? ["-hwaccel", "d3d11va"] : ["-hwaccel", "vaapi"];
    }

    /// <summary>A scale filter for the share encode: on the GPU for NVIDIA, on the CPU for the others (exports only, never while recording).</summary>
    public static string Scale(string gpu, int height) => Normalize(gpu) == "nvidia" ? $"scale_cuda=-2:{height}" : $"scale=-2:{height}";

    /// <summary>Quality-mode encoder options for an exact cut.</summary>
    public static string[] ExactEncode(string gpu) => Normalize(gpu) switch
    {
        "amd" => ["-quality", "quality", "-rc", "vbr_latency", "-qp_i", "21", "-qp_p", "21"],
        "intel" => ["-preset", "medium", "-global_quality", "21"],
        _ => ["-preset", "p4", "-rc", "vbr", "-cq", "21", "-b:v", "0"],
    };

    /// <summary>Bitrate-mode encoder options for a share-size copy.</summary>
    public static string[] ShareEncode(string gpu, int kbps) => Normalize(gpu) switch
    {
        "amd" => ["-quality", "quality", "-rc", "vbr_peak", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k"],
        "intel" => ["-preset", "slower", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k"],
        _ => ["-preset", "p6", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k"],
    };
}
