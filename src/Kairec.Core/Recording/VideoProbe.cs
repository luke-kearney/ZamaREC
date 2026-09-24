using System.Diagnostics;
using System.Globalization;

namespace Kairec.Core.Recording;

public static class VideoProbe
{
    public static async Task<double?> DurationSecondsAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        catch { return null; }
    }
}
