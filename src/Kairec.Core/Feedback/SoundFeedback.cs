using System.Diagnostics;
using Kairec.Core.Config;
using Microsoft.Extensions.Logging;

namespace Kairec.Core.Feedback;

/// <summary>
/// Audible-only feedback. Plays a short generated tone: via pw-play in a detached process on Linux, through
/// winmm's PlaySound on Windows (which has no volume control, so the Windows files are written at the volume).
/// Nothing visual, nothing that could take focus from the game.
/// </summary>
public sealed class SoundFeedback
{
    private readonly FeedbackConfig _cfg;
    private readonly ILogger _log;
    private readonly string _dir;

    public SoundFeedback(FeedbackConfig cfg, ILogger log)
    {
        _cfg = cfg; _log = log;
        _dir = Path.Combine(Paths.DataDir(), "sounds");
        Directory.CreateDirectory(_dir);
        _suffix = Platform.IsWindows ? $"-{(int)Math.Round(Math.Clamp(cfg.Volume, 0, 1) * 100)}" : "";
        _gain = Platform.IsWindows ? Math.Clamp(cfg.Volume, 0, 1) : 1;
        Ensure("mark", [(880, 0.08)]);
        Ensure("save", [(660, 0.07), (990, 0.10)]);
        Ensure("start", [(523, 0.07), (784, 0.09)]);
        Ensure("stop", [(784, 0.07), (523, 0.09)]);
        Ensure("error", [(220, 0.18)]);
    }

    private readonly string _suffix;
    private readonly double _gain;

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    public void Play(string name)
    {
        if (!_cfg.Sound) return;
        var file = Path.Combine(_dir, name + _suffix + ".wav");
        try
        {
            if (Platform.IsWindows)
            {
                const uint Async = 1, NoDefault = 2, FileName = 0x20000;
                PlaySound(file, IntPtr.Zero, Async | NoDefault | FileName);
                return;
            }
            var psi = new ProcessStartInfo("pw-play") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--volume"); psi.ArgumentList.Add(_cfg.Volume.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(file);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { _log.LogDebug(ex, "could not play {name}", name); }
    }

    private void Ensure(string name, (double Hz, double Seconds)[] notes)
    {
        var path = Path.Combine(_dir, name + _suffix + ".wav");
        if (File.Exists(path)) return;
        File.WriteAllBytes(path, Wav.Tone(notes, gain: _gain));
    }
}

internal static class Wav
{
    public static byte[] Tone((double Hz, double Seconds)[] notes, int rate = 48000, double gain = 1)
    {
        var samples = new List<short>();
        foreach (var (hz, secs) in notes)
        {
            var n = (int)(rate * secs);
            for (int i = 0; i < n; i++)
            {
                // 5 ms fade in/out to avoid clicks.
                var env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.005));
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * env * 0.6 * gain * short.MaxValue));
            }
        }
        var data = samples.Count * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}
