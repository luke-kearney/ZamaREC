using Kairec.Core.Config;

namespace Kairec.Core.Gsi;

/// <summary>Writes the gamestate_integration cfg CS2 reads at launch, and finds where it has to go.</summary>
public static class GsiConfigFile
{
    public const string FileName = "gamestate_integration_kairec.cfg";

    public static string Render(int port, string token) => $$"""
        "kairec"
        {
            "uri"        "http://127.0.0.1:{{port}}/gsi"
            "timeout"    "5.0"
            "buffer"     "0.05"
            "throttle"   "0.1"
            "heartbeat"  "15.0"
            "auth"
            {
                "token"  "{{token}}"
            }
            "data"
            {
                "provider"            "1"
                "map"                 "1"
                "round"               "1"
                "player_id"           "1"
                "player_state"        "1"
                "player_match_stats"  "1"
            }
        }

        """;

    /// <summary>Looks through every Steam library for the CS2 cfg folder.</summary>
    public static string? FindCs2CfgDir()
    {
        var home = Paths.Home();
        var roots = new[] { Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"), Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam") };
        var libs = new List<string>();
        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            libs.Add(r);
            var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in File.ReadLines(vdf))
            {
                var t = line.Trim();
                if (!t.StartsWith("\"path\"")) continue;
                var parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) libs.Add(parts[^1].Trim());
            }
        }
        foreach (var lib in libs.Distinct())
        {
            var cfg = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg");
            if (Directory.Exists(cfg)) return cfg;
        }
        return null;
    }

    /// <summary>Loads or creates the shared secret CS2 sends with every payload.</summary>
    public static string LoadOrCreateToken()
    {
        var p = Path.Combine(Paths.DataDir(), "gsi-token");
        if (File.Exists(p)) { var t = File.ReadAllText(p).Trim(); if (t.Length > 0) return t; }
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        File.WriteAllText(p, token);
        return token;
    }

    /// <summary>Writes the cfg if missing or different. Returns the path written, or null if no CS2 install was found.</summary>
    public static string? EnsureWritten(int port, string token, string? cfgDirOverride, out bool changed)
    {
        changed = false;
        var dir = string.IsNullOrEmpty(cfgDirOverride) ? FindCs2CfgDir() : Paths.Expand(cfgDirOverride);
        if (dir is null || !Directory.Exists(dir)) return null;
        var path = Path.Combine(dir, FileName);
        var content = Render(port, token);
        if (File.Exists(path) && File.ReadAllText(path) == content) return path;
        File.WriteAllText(path, content);
        changed = true;
        return path;
    }
}
