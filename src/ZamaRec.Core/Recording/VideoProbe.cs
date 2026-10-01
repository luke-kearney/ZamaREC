using System.Diagnostics;
using System.Globalization;

namespace ZamaRec.Core.Recording;

public static class VideoProbe
{
    public sealed record VideoInfo(int Width, int Height, double Fps);

    /// <summary>Size and frame rate of the first video stream, or null if ffprobe cannot read it.</summary>
    public static async Task<VideoInfo?> InfoAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg.Probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate", "-of", "csv=p=0", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var f = output.Trim().Split('\n')[0].Split(',');
            if (f.Length < 3 || !int.TryParse(f[0], out var w) || !int.TryParse(f[1], out var h)) return null;
            var r = f[2].Split('/');
            var fps = r.Length == 2 && double.TryParse(r[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? n / d : 60;
            return new VideoInfo(w, h, fps);
        }
        catch { return null; }
    }

    public static async Task<double?> DurationSecondsAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg.Probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        catch { return null; }
    }
}
