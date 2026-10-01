using System.Text.Json;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using ZamaRec.Protocol;

static class Daemon
{
    public static ControlResponse? Send(ControlRequest req)
    {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        sock.Connect(new UnixDomainSocketEndPoint(ControlPaths.SocketPath()));
        sock.Send(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, ProtocolJson.Default.ControlRequest) + "\n"));
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var n = sock.Receive(buf);
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (sb.ToString().Contains('\n')) break;
        }
        return JsonSerializer.Deserialize(sb.ToString().Split('\n')[0], ProtocolJson.Default.ControlResponse);
    }
}

/// <summary>
/// `zamarec top`: a live view refreshed every second until Ctrl+C.
/// Everything here is read from /proc and the filesystem. It never queries the GPU or the driver.
/// </summary>
static class Top
{
    private static readonly long TicksPerSec = 100;   // CLK_TCK on Linux x86_64
    private static readonly Dictionary<int, (long Cpu, long At)> _lastProc = new();
    private static (long Busy, long Total)? _lastSys;
    private static (long Bytes, long At)? _lastSessions;

    /// <summary>Prints the tail of events.jsonl. Reads the file directly, so it works even when the daemon is down.</summary>
    public static int PrintLog(int n, string? kind)
    {
        var path = Path.Combine(ControlPaths.DataDir(), "events.jsonl");
        if (!File.Exists(path)) { Console.WriteLine("no events yet"); return 0; }
        var lines = File.ReadAllLines(path);
        var shown = 0;
        var picked = new List<string>();
        for (int i = lines.Length - 1; i >= 0 && picked.Count < n; i--)
        {
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var r = doc.RootElement;
                var k = r.GetProperty("kind").GetString() ?? "";
                if (kind is not null && k != kind) continue;
                var ts = DateTimeOffset.Parse(r.GetProperty("ts").GetString()!).ToString("MM-dd HH:mm:ss");
                var prof = r.TryGetProperty("profile", out var p) ? p.GetString() : null;
                picked.Add($"{ts}  {k,-8} {r.GetProperty("message").GetString()}{(prof is null ? "" : $"  [{prof}]")}");
            }
            catch { }
        }
        picked.Reverse();
        foreach (var l in picked) { Console.WriteLine(l); shown++; }
        return 0;
    }

    public static async Task<int> RunAsync()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.Write("\x1b[?25l");   // hide cursor
        long sessionsBytes = 0, clipsBytes = 0, diskFree = 0; double writeRate = 0; var lastDisk = DateTime.MinValue;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                ControlResponse? resp = null; string? err = null;
                try { resp = Daemon.Send(new ControlRequest { Cmd = "status" }); }
                catch (SocketException) { err = "daemon not running"; }
                catch (Exception ex) { err = ex.Message; }
                var st = resp?.Status;

                if (st?.RootDir is string root && (DateTime.UtcNow - lastDisk).TotalSeconds >= 5)
                {
                    sessionsBytes = DirSize(Path.Combine(root, "sessions"));
                    clipsBytes = DirSize(Path.Combine(root, "clips"));
                    try { diskFree = new DriveInfo(root).AvailableFreeSpace; } catch { }
                    var now = Environment.TickCount64;
                    if (_lastSessions is { } l && now > l.At) writeRate = (sessionsBytes - l.Bytes) * 1000.0 / (now - l.At);
                    _lastSessions = (sessionsBytes, now);
                    lastDisk = DateTime.UtcNow;
                }

                var sb = new StringBuilder();
                sb.Append("\x1b[H\x1b[2J");
                sb.AppendLine($"zamarec top   {DateTime.Now:HH:mm:ss}   (Ctrl+C to quit)");
                sb.AppendLine();
                if (err is not null) { sb.AppendLine($"  {err}"); }
                else if (st is not null)
                {
                    if (st.State == "idle") sb.AppendLine($"  state     idle   profiles: {string.Join(", ", st.Profiles)}");
                    else
                    {
                        sb.AppendLine($"  state     {st.State}   {st.Profile} ({st.Mode})   since {st.SessionStartedAt?.ToString("HH:mm:ss")}   elapsed {Fmt(st.ElapsedSeconds)}");
                        if (st.RecordedSeconds is double r) sb.AppendLine($"  timeline  recorded {Fmt(r)}   in window {Fmt(st.WindowSeconds)}   markers {st.MarkerCount}");
                        else sb.AppendLine($"  buffer    markers {st.MarkerCount}   pending clip marks {st.PendingMarks}");
                        sb.AppendLine($"  session   {st.SessionDir}");
                    }
                }
                sb.AppendLine();

                var sys = SysCpu();
                var (memTotal, memAvail) = MemInfo();
                sb.AppendLine($"  {"process",-22}{"pid",8}{"cpu%",8}{"rss",10}{"threads",9}");
                double cpuSum = 0; long rssSum = 0;
                if (st is not null)
                {
                    foreach (var (name, pid) in Procs(st))
                    {
                        var p = ProcStat(pid);
                        if (p is null) continue;
                        cpuSum += p.Value.Cpu; rssSum += p.Value.Rss;
                        sb.AppendLine($"  {name,-22}{pid,8}{p.Value.Cpu,8:0.0}{Bytes(p.Value.Rss),10}{p.Value.Threads,9}");
                    }
                    sb.AppendLine($"  {"zamarec total",-22}{"",8}{cpuSum,8:0.0}{Bytes(rssSum),10}");
                }
                sb.AppendLine($"  {"system",-22}{"",8}{sys,8:0.0}{Bytes(memTotal - memAvail),10}   of {Bytes(memTotal)} used");
                sb.AppendLine();
                sb.AppendLine($"  storage   sessions {Bytes(sessionsBytes)}   clips {Bytes(clipsBytes)}   free {Bytes(diskFree)}   writing {Bytes((long)Math.Max(0, writeRate))}/s ({(writeRate * 3600 / 1e9):0.0} GB/h)");
                sb.AppendLine();
                sb.AppendLine(OperatingSystem.IsWindows() ? "  cpu% is of one core. Figures come from the process table only; the GPU is never queried."
                                                          : "  cpu% is of one core. Figures come from /proc only; the GPU is never queried.");
                Console.Out.Write(sb.ToString());
                Console.Out.Flush();
                try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { }
            }
        }
        finally { Console.Write("\x1b[?25h\n"); }
        return 0;
    }

    private static IEnumerable<(string Name, int Pid)> Procs(DaemonStatus st)
    {
        yield return ("daemon", st.DaemonPid);
        if (st.RecorderPid is int rp)
        {
            yield return (OperatingSystem.IsWindows() ? "ffmpeg" : "gpu-screen-recorder", rp);
            if (!OperatingSystem.IsWindows()) foreach (var child in Children(rp)) yield return (child.Comm, child.Pid);
        }
    }

    private static List<(int Pid, string Comm)> Children(int parent)
    {
        var list = new List<(int, string)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = stat.LastIndexOf(')');
                var fields = stat[(close + 2)..].Split(' ');
                if (int.Parse(fields[1]) == parent) list.Add((pid, stat[(stat.IndexOf('(') + 1)..close]));
            }
            catch { }
        }
        return list;
    }

    private static (double Cpu, long Rss, int Threads)? ProcStat(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                var ms = (long)p.TotalProcessorTime.TotalMilliseconds;
                var now = Environment.TickCount64;
                double pct = 0;
                if (_lastProc.TryGetValue(pid, out var last) && now > last.At) pct = (ms - last.Cpu) * 100.0 / (now - last.At);
                _lastProc[pid] = (ms, now);
                return (pct, p.WorkingSet64, p.Threads.Count);
            }
            catch { return null; }
        }
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            var f = stat[(close + 2)..].Split(' ');
            var cpu = long.Parse(f[11]) + long.Parse(f[12]);          // utime + stime (fields 14, 15)
            var threads = int.Parse(f[17]);
            var rss = long.Parse(f[21]) * Environment.SystemPageSize;  // field 24
            var now = Environment.TickCount64;
            double pct = 0;
            if (_lastProc.TryGetValue(pid, out var last) && now > last.At)
                pct = (cpu - last.Cpu) * 1000.0 / TicksPerSec / (now - last.At) * 100.0;
            _lastProc[pid] = (cpu, now);
            return (pct, rss, threads);
        }
        catch { return null; }
    }

    private static double SysCpu()
    {
        if (OperatingSystem.IsWindows()) return 0;   // not shown there
        try
        {
            var line = File.ReadLines("/proc/stat").First();
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            var idle = v[3] + v[4]; var total = v.Sum(); var busy = total - idle;
            double pct = 0;
            if (_lastSys is { } l && total > l.Total) pct = (busy - l.Busy) * 100.0 / (total - l.Total);
            _lastSys = (busy, total);
            return pct;
        }
        catch { return 0; }
    }

    private static (long Total, long Avail) MemInfo()
    {
        long total = 0, avail = 0;
        if (OperatingSystem.IsWindows())
        {
            try { var gc = GC.GetGCMemoryInfo(); return (gc.TotalAvailableMemoryBytes, Math.Max(0, gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes)); } catch { return (0, 0); }
        }
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) total = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
                else if (line.StartsWith("MemAvailable:")) avail = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
            }
        }
        catch { }
        return (total, avail);
    }

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch { } } }
        catch { }
        return total;
    }

    private static string Bytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB"
        : b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
        : (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KiB";

    private static string Fmt(double? s)
    {
        if (s is null) return "–";
        var t = TimeSpan.FromSeconds(s.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
