using System.Runtime.InteropServices;

namespace Kairec.Core;

/// <summary>CLOCK_MONOTONIC in microseconds, the same clock gpu-screen-recorder writes into its .ts file.</summary>
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
        if (clock_gettime(ClockMonotonic, out var ts) != 0) throw new InvalidOperationException("clock_gettime failed");
        return ts.Sec * 1_000_000 + ts.Nsec / 1000;
    }

    public static long RealtimeMicros()
    {
        if (clock_gettime(ClockRealtime, out var ts) != 0) throw new InvalidOperationException("clock_gettime failed");
        return ts.Sec * 1_000_000 + ts.Nsec / 1000;
    }
}
