using Kairec.Core.Config;

namespace Kairec.Core.Processes;

/// <summary>
/// Reads what Steam leaves on disk and in /proc: the library folders, an app's name from its manifest, and the
/// app id of a launch. Steam starts every game under "reaper SteamLaunch AppId=N -- command...". Nothing here
/// talks to the Steam client or needs the overlay; without Steam it all just finds nothing.
/// </summary>
public static class SteamLibrary
{
    public const string LaunchComm = "reaper";

    /// <summary>Steam tools that are launched like games but are not worth recording.</summary>
    public static readonly IReadOnlySet<long> Tools = new HashSet<long>
    {
        228980,     // Steamworks Common Redistributables
        250820,     // SteamVR
        431960,     // Wallpaper Engine
    };

    /// <summary>Compatibility tools and runtimes, which come in too many versions to list by id.</summary>
    public static bool IsToolName(string name) =>
        name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Steamworks ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The app id from a launch wrapper's argv (NUL or space separated), or null if this is not one.</summary>
    public static long? ParseLaunch(string cmdline)
    {
        var args = cmdline.Split(['\0', ' '], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < args.Length && i < 3; i++)
        {
            if (args[i] != "SteamLaunch") continue;
            return args[i + 1].StartsWith("AppId=", StringComparison.Ordinal) && long.TryParse(args[i + 1].AsSpan(6), out var id) && id > 0 ? id : null;
        }
        return null;
    }

    /// <summary>The Steam app a process was launched for (SteamAppId in its environment), read once when a session starts.</summary>
    public static long? AppIdOfProcess(int pid, string procRoot = "/proc")
    {
        if (Platform.IsWindows) { var app = WindowsProcesses.RunningSteamApp(); return app > 0 ? app : null; }   // the client says which app it is running
        try
        {
            foreach (var kv in File.ReadAllText(Path.Combine(procRoot, pid.ToString(), "environ")).Split('\0'))
                if (kv.StartsWith("SteamAppId=", StringComparison.Ordinal) && long.TryParse(kv.AsSpan(11), out var id) && id > 0) return id;
        }
        catch { }
        return null;
    }

    /// <summary>Every Steam library folder: the install itself plus the ones listed in libraryfolders.vdf.</summary>
    public static List<string> Libraries(string? home = null)
    {
        home ??= Paths.Home();
        var roots = Platform.IsWindows
            ? (WindowsProcesses.SteamPath() is { } sp ? new[] { sp } : Array.Empty<string>())
            : new[] { Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"), Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam") };
        var libs = new List<string>();
        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            libs.Add(r);
            var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (var line in File.ReadLines(vdf))
                    if (VdfValue(line, "path") is { } path) libs.Add(path);
            }
            catch (IOException) { }
        }
        return libs.Distinct().ToList();
    }

    /// <summary>The installed app's name from appmanifest_N.acf, tidied for use as a profile name. Null if no library has it.</summary>
    public static string? AppName(long appId, IEnumerable<string>? libraries = null)
    {
        foreach (var lib in libraries ?? Libraries())
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                foreach (var line in File.ReadLines(manifest))
                    if (VdfValue(line, "name") is { } name && SafeName(name) is { Length: > 0 } safe) return safe;
            }
            catch (IOException) { }
        }
        return null;
    }

    /// <summary>A name that works as a folder: no separators, control characters or trademark signs.</summary>
    public static string SafeName(string name)
    {
        var chars = name.Where(c => c is not ('™' or '®' or '©') && !char.IsControl(c)).Select(c => c is '/' or '\\' ? '-' : c).ToArray();
        return new string(chars).Trim().TrimStart('.');
    }

    // One `"key"  "value"` line of Valve's text format.
    private static string? VdfValue(string line, string key)
    {
        var t = line.Trim();
        if (!t.StartsWith($"\"{key}\"", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[^1].Replace("\\\\", "\\").Trim() is { Length: > 0 } v ? v : null;
    }
}
