using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace ZamaRec.Core.Processes;

/// <summary>
/// The Windows side of process and Steam detection. Processes come from the process table (one system call per
/// scan, no per-process handles unless a profile matches on the command line); Steam's running game comes from
/// the registry value the client keeps (RunningAppID), which needs no pid at all. Nothing touches the GPU.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsProcesses
{
    public readonly record struct Entry(int Pid, string Name);

    /// <summary>Every process: pid and image name without .exe (what a profile's `match` sees).</summary>
    public static List<Entry> List(int selfPid)
    {
        var list = new List<Entry>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.Id != selfPid && p.Id > 4) list.Add(new Entry(p.Id, p.ProcessName)); }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    public static bool IsAlive(int pid)
    {
        if (pid < 0) return RunningSteamApp() == -pid;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- command line (PEB read; same-user processes only)

    private static readonly Dictionary<int, string> _cmdlines = new();

    /// <summary>The process's command line, or its image path when that cannot be read. Cached per pid.</summary>
    public static string CommandLine(int pid)
    {
        lock (_cmdlines)
        {
            if (_cmdlines.TryGetValue(pid, out var c)) return c;
            var line = ReadCommandLine(pid) ?? ImagePath(pid) ?? "";
            if (_cmdlines.Count > 4096) _cmdlines.Clear();
            _cmdlines[pid] = line;
            return line;
        }
    }

    public static void ForgetGone(HashSet<int> alive) { lock (_cmdlines) foreach (var k in _cmdlines.Keys.Where(k => !alive.Contains(k)).ToList()) _cmdlines.Remove(k); }

    private static string? ImagePath(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.MainModule?.FileName; } catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation { public IntPtr Reserved1; public IntPtr PebBaseAddress; public IntPtr Reserved2a, Reserved2b; public IntPtr UniquePid; public IntPtr Reserved3; }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref ProcessBasicInformation pbi, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static string? ReadCommandLine(int pid)
    {
        if (IntPtr.Size != 8) return null;
        const uint QueryInformation = 0x0400, VmRead = 0x0010;
        var h = OpenProcess(QueryInformation | VmRead, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;
            var ptr = new byte[8];
            if (!ReadProcessMemory(h, pbi.PebBaseAddress + 0x20, ptr, (IntPtr)8, out _)) return null;   // PEB.ProcessParameters
            var parameters = (IntPtr)BitConverter.ToInt64(ptr);
            var us = new byte[16];
            if (!ReadProcessMemory(h, parameters + 0x70, us, (IntPtr)16, out _)) return null;             // RTL_USER_PROCESS_PARAMETERS.CommandLine (UNICODE_STRING)
            var length = BitConverter.ToUInt16(us, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(us, 8);
            if (length == 0 || buffer == IntPtr.Zero || length > 32 * 1024) return null;
            var text = new byte[length];
            if (!ReadProcessMemory(h, buffer, text, (IntPtr)length, out _)) return null;
            return Encoding.Unicode.GetString(text);
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- Steam

    /// <summary>The app Steam is running right now (its RunningAppID value), or 0.</summary>
    public static long RunningSteamApp()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return k?.GetValue("RunningAppID") is int id && id > 0 ? id : 0;
        }
        catch { return 0; }
    }

    /// <summary>Steam's install folder from the registry, if Steam is installed.</summary>
    public static string? SteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string p && p.Length > 0) return p.Replace('/', '\\');
        }
        catch { }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var d = Path.Combine(pf, "Steam");
        return Directory.Exists(d) ? d : null;
    }
}
