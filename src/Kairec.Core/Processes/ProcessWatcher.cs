using System.Text.RegularExpressions;
using Kairec.Core.Config;
using Microsoft.Extensions.Logging;

namespace Kairec.Core.Processes;

public sealed record GameProcess(ProfileConfig Profile, int Pid, string Comm);

/// <summary>
/// Finds game processes by scanning /proc. This is the only polling the daemon does: a CPU-side directory
/// listing once a second, reading /proc/PID/comm (and cmdline only for profiles that need it).
/// It never touches the GPU, the driver, or any hardware counters.
/// </summary>
public sealed class ProcessWatcher
{
    private readonly ILogger _log;
    private readonly List<(ProfileConfig Profile, Regex? Comm, Regex? Cmdline)> _matchers = new();

    public ProcessWatcher(IEnumerable<ProfileConfig> profiles, ILogger log)
    {
        _log = log;
        foreach (var p in profiles)
        {
            _matchers.Add((p,
                p.Match is null ? null : new Regex(p.Match, RegexOptions.Compiled | RegexOptions.CultureInvariant),
                p.MatchCmdline is null ? null : new Regex(p.MatchCmdline, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)));
        }
    }

    public GameProcess? FindRunning(int selfPid)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid) || pid == selfPid) continue;
            string comm;
            try { comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n'); }
            catch { continue; }
            string? cmdline = null;
            foreach (var (profile, commRe, cmdRe) in _matchers)
            {
                if (commRe is not null && commRe.IsMatch(comm)) return new GameProcess(profile, pid, comm);
                if (cmdRe is null) continue;
                cmdline ??= ReadCmdline(dir);
                if (cmdline.Length > 0 && cmdRe.IsMatch(cmdline)) return new GameProcess(profile, pid, comm);
            }
        }
        return null;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            // A zombie still has a /proc entry; treat it as gone.
            var status = File.ReadAllText($"/proc/{pid}/status");
            var idx = status.IndexOf("State:", StringComparison.Ordinal);
            if (idx < 0) return false;
            var state = status[(idx + 6)..].TrimStart();
            return state.Length > 0 && state[0] != 'Z';
        }
        catch { return false; }
    }

    private static string ReadCmdline(string procDir)
    {
        try { return File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' '); }
        catch { return ""; }
    }
}
