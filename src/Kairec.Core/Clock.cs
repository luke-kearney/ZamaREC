using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kairec.Core;

/// <summary>
/// A monotonic clock in microseconds. On Linux it is CLOCK_MONOTONIC, the clock gpu-screen-recorder writes into
/// its .ts file; on Windows it is the performance counter, and the ffmpeg recorder stamps its first frame on the same clock.
/// </summary>
public static class MonotonicClock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public long Sec; public long Nsec; }

    [DllImport("libc", SetLastError = true)]
    private static extern int clock_gettime(int clockId, out Timespec ts);

    private const int ClockMonotonic = 1;
    private const int ClockRealtime = 0;

    public static long NowMicros()
    {
        if (Platform.IsWindows) return Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000);
        if (clock_gettime(ClockMonotonic, out var ts) != 0) throw new InvalidOperationException("clock_gettime failed");
        return ts.Sec * 1_000_000 + ts.Nsec / 1000;
    }

    public static long RealtimeMicros()
    {
        if (Platform.IsWindows) return (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / 10;
        if (clock_gettime(ClockRealtime, out var ts) != 0) throw new InvalidOperationException("clock_gettime failed");
        return ts.Sec * 1_000_000 + ts.Nsec / 1000;
    }
}
