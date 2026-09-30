namespace Kairec.Core.Telemetry;

/// <summary>CPU and memory of a set of processes, from /proc (Windows: the process's own counters). CPU% is per core, averaged since the previous sample.</summary>
public sealed class ProcessUsage
{
    private readonly Dictionary<int, (long Cpu, long At)> _last = new();
    private const long TicksPerSec = 100;

    public sealed record Sample(double CpuPercent, long RssBytes, int Processes);

    public Sample Measure(IEnumerable<int> pids)
    {
        double cpu = 0; long rss = 0; int n = 0;
        var now = Environment.TickCount64;
        var seen = new HashSet<int>();
        foreach (var pid in pids)
        {
            try
            {
                if (Platform.IsWindows)
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(pid);
                    var ms = (long)proc.TotalProcessorTime.TotalMilliseconds;
                    rss += proc.WorkingSet64;
                    if (_last.TryGetValue(pid, out var lw) && now > lw.At) cpu += (ms - lw.Cpu) * 100.0 / (now - lw.At);
                    _last[pid] = (ms, now); seen.Add(pid); n++;
                    continue;
                }
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var ticks = long.Parse(f[11]) + long.Parse(f[12]);
                rss += long.Parse(f[21]) * Environment.SystemPageSize;
                if (_last.TryGetValue(pid, out var l) && now > l.At) cpu += (ticks - l.Cpu) * 1000.0 / TicksPerSec / (now - l.At) * 100.0;
                _last[pid] = (ticks, now); seen.Add(pid); n++;
            }
            catch { }
        }
        foreach (var k in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(k);
        return new Sample(Math.Round(cpu, 1), rss, n);
    }

    /// <summary>Direct children of a pid (the recorder's kms helper).</summary>
    public static List<int> Children(int parent)
    {
        var list = new List<int>();
        if (Platform.IsWindows) return list;   // ffmpeg has no helper process
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (int.Parse(f[1]) == parent) list.Add(pid);
            }
            catch { }
        }
        return list;
    }

    public static long DirBytes(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }) : 0; }
        catch { return 0; }
    }
}
