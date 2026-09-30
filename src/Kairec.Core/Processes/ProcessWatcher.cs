using System.Text.RegularExpressions;
using Kairec.Core.Config;
using Microsoft.Extensions.Logging;

namespace Kairec.Core.Processes;

public sealed record GameProcess(ProfileConfig Profile, int Pid, string Comm);

/// <summary>A game Steam has launched: the pid of its launch wrapper, which lives until the whole game tree is gone.</summary>
public sealed record SteamLaunch(int Pid, long AppId);

/// <summary>One pass over /proc: the first process a profile matches, else every Steam launch that is running.</summary>
public sealed record ProcessScan(GameProcess? Match, IReadOnlyList<SteamLaunch> Launches);

/// <summary>
/// Finds game processes by scanning /proc. This is the only polling the daemon does: a CPU-side directory
/// listing once a second, reading /proc/PID/comm (and cmdline only for profiles that need it, and for Steam's
/// launch wrapper). It never touches the GPU, the driver, or any hardware counters.
/// On Windows the same scan reads the process table, and Steam's running game comes from the registry as a
/// launch with a negative pid (-appId), which <see cref="IsAlive"/> understands.
/// </summary>
public sealed class ProcessWatcher
{
    private readonly ILogger _log;
    private readonly string _proc;
    private readonly List<(ProfileConfig Profile, Regex? Comm, Regex? Cmdline)> _matchers = new();

    public ProcessWatcher(IEnumerable<ProfileConfig> profiles, ILogger log, string procRoot = "/proc")
    {
        _log = log; _proc = procRoot;
        foreach (var p in profiles)
        {
            if (p.Match is null && p.MatchCmdline is null) continue;   // matched by steam_app_id instead
            _matchers.Add((p,
                p.Match is null ? null : new Regex(p.Match, RegexOptions.Compiled | RegexOptions.CultureInvariant),
                p.MatchCmdline is null ? null : new Regex(p.MatchCmdline, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)));
        }
    }

    public GameProcess? FindRunning(int selfPid) => Scan(selfPid, steam: false).Match;

    /// <summary>Stops at the first profile match, since that wins over anything Steam has running.</summary>
    public ProcessScan Scan(int selfPid, bool steam = true)
    {
        if (Platform.IsWindows) return ScanWindows(selfPid, steam);
        var launches = new List<SteamLaunch>();
        foreach (var dir in Directory.EnumerateDirectories(_proc))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid) || pid == selfPid) continue;
            string comm;
            try { comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n'); }
            catch { continue; }
            string? cmdline = null;
            foreach (var (profile, commRe, cmdRe) in _matchers)
            {
                if (commRe is not null && commRe.IsMatch(comm)) return new ProcessScan(new GameProcess(profile, pid, comm), launches);
                if (cmdRe is null) continue;
                cmdline ??= ReadCmdline(dir);
                if (cmdline.Length > 0 && cmdRe.IsMatch(cmdline)) return new ProcessScan(new GameProcess(profile, pid, comm), launches);
            }
            if (steam && comm == SteamLibrary.LaunchComm && SteamLibrary.ParseLaunch(cmdline ?? ReadCmdline(dir)) is long appId)
                launches.Add(new SteamLaunch(pid, appId));
        }
        return new ProcessScan(null, launches);
    }

    private ProcessScan ScanWindows(int selfPid, bool steam)
    {
        var launches = steam ? SteamLaunches() : new List<SteamLaunch>();
        var procs = WindowsProcesses.List(selfPid);
        var needCmd = _matchers.Any(m => m.Cmdline is not null);
        if (needCmd) WindowsProcesses.ForgetGone(procs.Select(p => p.Pid).ToHashSet());
        foreach (var (pid, name) in procs)
        {
            string? cmdline = null;
            foreach (var (profile, commRe, cmdRe) in _matchers)
            {
                if (commRe is not null && commRe.IsMatch(name)) return new ProcessScan(new GameProcess(profile, pid, name), launches);
                if (cmdRe is null) continue;
                cmdline ??= WindowsProcesses.CommandLine(pid);
                if (cmdline.Length > 0 && cmdRe.IsMatch(cmdline)) return new ProcessScan(new GameProcess(profile, pid, name), launches);
            }
        }
        return new ProcessScan(null, launches);
    }

    /// <summary>Every process, for the profile picker in Settings. On demand only.</summary>
    public static List<(int Pid, string Comm, string Cmdline)> ListAll(int selfPid)
    {
        var list = new List<(int, string, string)>();
        if (Platform.IsWindows)
        {
            foreach (var (pid, name) in WindowsProcesses.List(selfPid)) list.Add((pid, name, WindowsProcesses.CommandLine(pid)));
            return list;
        }
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == selfPid) continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n');
                var cmd = ReadCmdline(dir).Trim();
                if (cmd.Length == 0) continue;   // kernel threads
                var uid = File.ReadLines(Path.Combine(dir, "status")).FirstOrDefault(l => l.StartsWith("Uid:"))?.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                if (uid != OwnUid()) continue;   // only my own processes
                list.Add((pid, comm, cmd));
            }
            catch { }
        }
        return list;
    }

    private static string? _uid;
    private static string OwnUid()
    {
        if (_uid is not null) return _uid;
        try { _uid = File.ReadLines("/proc/self/status").First(l => l.StartsWith("Uid:")).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1]; } catch { _uid = ""; }
        return _uid;
    }

    /// <summary>Every Steam launch running right now, whether or not a profile matches something.</summary>
    public List<SteamLaunch> SteamLaunches()
    {
        var launches = new List<SteamLaunch>();
        if (Platform.IsWindows)
        {
            var app = WindowsProcesses.RunningSteamApp();
            if (app > 0 && app <= int.MaxValue) launches.Add(new SteamLaunch((int)-app, app));
            return launches;
        }
        foreach (var dir in Directory.EnumerateDirectories(_proc))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n') == SteamLibrary.LaunchComm && SteamLibrary.ParseLaunch(ReadCmdline(dir)) is long appId)
                    launches.Add(new SteamLaunch(pid, appId));
            }
            catch { }
        }
        return launches;
    }

    public static bool IsAlive(int pid)
    {
        if (Platform.IsWindows) return WindowsProcesses.IsAlive(pid);
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
