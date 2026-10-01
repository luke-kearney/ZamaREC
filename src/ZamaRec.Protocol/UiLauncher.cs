using System.Diagnostics;

namespace ZamaRec.Protocol;

/// <summary>
/// Opens the web UI when the user asks for it (the menu entry, the tray, `zamarec ui`), never on its own.
/// "app" mode gives it a window of its own: a Chromium-family browser started with --app and a profile kept
/// under zamarec's data dir, so it shares nothing with the everyday browser. If that window is already open it
/// is brought to the front instead of opening a second one. Without such a browser, or in "browser" mode, the
/// page opens in the default browser as before.
/// </summary>
public static class UiLauncher
{
    public const string DefaultUrl = "http://127.0.0.1:3556/";

    /// <summary>
    /// The size the window opens at: the narrowest at which nothing in the page wraps, shrinks or is cut off.
    /// What sets it is the transport bar at its fullest (mixer button, an hour-long in/out range: 976 px), plus
    /// the viewer's padding (42) and the two side panes (320 + 340) = 1678. The height then shows the video at
    /// 16:9 with the timeline below it. Measured in Chrome; re-measure if the header or transport bar change.
    /// </summary>
    public const int WindowWidth = 1680, WindowHeight = 910;

    private static readonly string[] Browsers =
        ["google-chrome-stable", "google-chrome", "chromium", "chromium-browser", "brave", "brave-browser", "microsoft-edge-stable", "vivaldi-stable"];

    /// <summary>Windows: Chrome, then Edge (which every Windows 10/11 has), then Brave, under their usual install folders.</summary>
    private static IEnumerable<string> WindowsBrowsers()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
            .Where(r => r.Length > 0).Distinct();
        foreach (var r in roots)
        {
            yield return Path.Combine(r, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(r, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(r, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
        }
    }

    /// <summary>The browser used for the app window: ZAMAREC_UI_BROWSER, or the first Chromium-family one on PATH (Windows: in its install folder).</summary>
    public static string? FindBrowser()
    {
        var forced = Environment.GetEnvironmentVariable("ZAMAREC_UI_BROWSER");
        if (!string.IsNullOrEmpty(forced)) return OnPath(forced) ?? (File.Exists(forced) ? forced : null);
        if (OperatingSystem.IsWindows()) return WindowsBrowsers().FirstOrDefault(File.Exists) ?? new[] { "chrome.exe", "msedge.exe" }.Select(OnPath).FirstOrDefault(p => p is not null);
        return Browsers.Select(OnPath).FirstOrDefault(p => p is not null);
    }

    public static string ProfileDir() => Path.Combine(ControlPaths.DataDir(), "ui-profile");

    /// <summary>The Wayland app id (X11 class) Chromium gives an --app window: chrome-HOST_PATH-Default, "/" written as "_".</summary>
    public static string WindowClassSuffix(string url)
    {
        var u = new Uri(url);
        return $"-{u.Host}_{u.AbsolutePath.Replace('/', '_')}-Default";
    }

    /// <summary>Opens or raises the UI. Returns "window", "focused" or "browser" for what happened, or an error text.</summary>
    public static string Open(string url, string? mode)
    {
        if (!string.Equals(mode, "browser", StringComparison.OrdinalIgnoreCase) && FindBrowser() is { } browser)
        {
            var profile = ProfileDir();
            if (OperatingSystem.IsWindows() && WindowsRaise.Raise("ZamaREC")) return "focused";
            if (!OperatingSystem.IsWindows() && IsRunning(profile) && Raise(url)) return "focused";
            Directory.CreateDirectory(profile);
            string[] args = [$"--app={url}", $"--user-data-dir={profile}", $"--window-size={WindowWidth},{WindowHeight}", "--class=zamarec", "--no-first-run", "--no-default-browser-check"];
            if (OperatingSystem.IsWindows()) return SpawnDetached(browser, args) ? "window" : "could not start the browser";
            // Through systemd-run where there is one: started from the tray, the window would otherwise live in the
            // tray service's cgroup and close whenever that service restarts.
            if (OnPath("systemd-run") is { } sd
                ? Spawn(sd, ["--user", "--quiet", "--collect", "-E", "WAYLAND_DISPLAY", "-E", "DISPLAY", "-E", "XDG_CURRENT_DESKTOP", "--", browser, .. args], wait: true) || SpawnDetached(browser, args)
                : SpawnDetached(browser, args)) return "window";
        }
        if (OperatingSystem.IsWindows())
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return "browser"; }
            catch { return "could not open a browser"; }
        }
        return Spawn("xdg-open", [url]) ? "browser" : "could not start a browser (no Chromium-family browser and no xdg-open)";
    }

    /// <summary>Chromium keeps SingletonLock -> "host-pid" in its profile while it runs; a stale one survives a crash.</summary>
    private static bool IsRunning(string profile)
    {
        try
        {
            var link = new FileInfo(Path.Combine(profile, "SingletonLock")).LinkTarget;
            return link is not null && int.TryParse(link[(link.LastIndexOf('-') + 1)..], out var pid) && Directory.Exists($"/proc/{pid}");
        }
        catch { return false; }
    }

    /// <summary>
    /// Brings the open window to the front. Wayland lets only the compositor do that, so this asks KWin to run
    /// a three-line script; on another desktop it fails and a new window is opened instead.
    /// </summary>
    private static bool Raise(string url)
    {
        try
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
            var script = Path.Combine(runtime, "zamarec-raise-ui.js");
            File.WriteAllText(script, $$"""
                const w = workspace.windowList().find(w => w.resourceClass.endsWith("{{WindowClassSuffix(url)}}"));
                if (w) { w.minimized = false; workspace.activeWindow = w; }
                """);
            const string name = "zamarec-raise-ui";
            Run("busctl", ["--user", "call", "org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s", name]);   // left over from a failed run
            var loaded = Run("busctl", ["--user", "call", "org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss", script, name]);
            if (loaded.Code != 0) return false;
            var id = loaded.Out.Trim().Split(' ').LastOrDefault();
            var ran = Run("busctl", ["--user", "call", "org.kde.KWin", $"/Scripting/Script{id}", "org.kde.kwin.Script", "run"]).Code == 0;
            Run("busctl", ["--user", "call", "org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s", name]);
            return ran;
        }
        catch { return false; }
    }

    private static string? OnPath(string exe)
    {
        if (exe.Contains('/') || exe.Contains('\\')) return File.Exists(exe) ? exe : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <param name="wait">Wait for a short-lived launcher to finish and report whether it succeeded.</param>
    private static bool Spawn(string exe, IEnumerable<string> args, bool wait = false)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!wait) return true;
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // A browser outlives us and keeps writing to its output: hand it /dev/null, not a pipe that closes when we exit.
    private static bool SpawnDetached(string exe, IEnumerable<string> args)
    {
        if (!OperatingSystem.IsWindows()) return Spawn("sh", ["-c", "exec \"$@\" >/dev/null 2>&1 </dev/null &", "sh", exe, .. args]);
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch { return false; }
    }

    private static (int Code, string Out) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return (p.HasExited ? p.ExitCode : -1, output);
    }
}
