using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZamaRec.Core.Export;

namespace ZamaRec.Tests;

/// <summary>
/// A frame-exact cut from a clip keeps its sound in step with the picture. Needs ffmpeg with libx264 and an NVIDIA GPU (the head is
/// encoded with NVENC), so it does nothing on a machine without them, CI included.
/// </summary>
public class ClipCutAudioTests
{
    [Fact]
    public async Task FrameExactCutFromAClipKeepsTheAudioInStep()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/nvidia0") || Which("ffmpeg") is null) return;
        var dir = Directory.CreateTempSubdirectory("zamarec-test").FullName;
        try
        {
            // 12 s at 60 fps with a keyframe every second and no B-frames, like the recorder's; a 50 ms beep on every whole second
            var src = Path.Combine(dir, "src.mp4");
            Assert.Equal(0, Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=60",
                "-f", "lavfi", "-i", "aevalsrc=sin(2*PI*1000*t)*lt(mod(t\\,1)\\,0.05):s=48000", "-t", "12",
                "-c:v", "libx264", "-profile:v", "high", "-bf", "0", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "128k", src));

            // In at 7.55: the keyframe before it is at 7.0, the next beep at 8.0, so 0.45 s into the cut
            var log = new Collect();
            var outPath = Path.Combine(dir, "cut.mp4");
            var r = await ClipExporter.ExportFromFileAsync(src, 7.55, 9.55, outPath, exact: true, log, CancellationToken.None);
            Assert.True(r.Ok, r.Error);
            Assert.Contains(log.Lines, l => l.Contains("copied the rest"));   // the smart cut ran, not the whole-range re-encode

            var raw = Path.Combine(dir, "a.raw");
            Assert.Equal(0, Run("ffmpeg", "-v", "error", "-y", "-i", outPath, "-map", "0:a:0", "-ac", "1", "-ar", "8000", "-f", "s16le", raw));
            var pcm = File.ReadAllBytes(raw);
            var samples = Enumerable.Range(0, pcm.Length / 2).Select(i => Math.Abs((int)BitConverter.ToInt16(pcm, i * 2))).ToArray();
            var onset = Array.FindIndex(samples, s => s > 8000) / 8000.0;
            Assert.InRange(onset, 0.42, 0.48);   // without the fix the sound began at the keyframe, 0.55 s early: the 7.0 beep came first
            Assert.InRange(samples.Length / 8000.0, 1.95, 2.05);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class Collect : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static int Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}
