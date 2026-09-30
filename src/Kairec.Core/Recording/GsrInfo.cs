using System.Diagnostics;

namespace Kairec.Core.Recording;

/// <summary>One-off queries to gpu-screen-recorder for the settings page. Run only on demand, never periodically.</summary>
public static class GsrInfo
{
    public sealed record Device(string Id, string Description);

    public static async Task<List<Device>> MonitorsAsync(CancellationToken ct)
    {
        var lines = await RunAsync("--list-monitors", ct);
        return lines.Select(l => l.Split('|')).Where(p => p.Length >= 1 && p[0].Length > 0)
            .Select(p => new Device(p[0], p.Length > 1 ? p[1] : "")).ToList();
    }

    public static async Task<List<Device>> AudioDevicesAsync(CancellationToken ct)
    {
        var lines = await RunAsync("--list-audio-devices", ct);
        return lines.Select(l => l.Split('|')).Where(p => p.Length >= 2).Select(p => new Device(p[0], p[1])).ToList();
    }

    /// <summary>Applications playing or recording audio right now, by the name app: and app-inverse: sources match.</summary>
    public static async Task<List<string>> AudioApplicationsAsync(CancellationToken ct) =>
        (await RunAsync("--list-application-audio", ct)).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static async Task<List<string>> RunAsync(string flag, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("gpu-screen-recorder") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(flag);
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        }
        catch { return new List<string>(); }
    }
}
