using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ZamaRec.Core.Config;
using ZamaRec.Protocol;
using Microsoft.Win32;

namespace ZamaRec.Daemon;

/// <summary>
/// The daemon's presence on a Windows desktop: a tray icon with a menu, and the global hotkeys from [hotkeys].
/// This is the Windows counterpart of the KDE shortcuts plus the Linux tray, built straight on Win32 so the
/// self-contained exe needs nothing else. It never opens a window on its own: the UI opens only from the menu,
/// a double-click on the icon, or the ui hotkey. Feedback stays sound only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShell : IHostedService
{
    private readonly ZamaRecConfig _cfg;
    private readonly RecordingCoordinator _rec;
    private readonly PauseControl _pause;
    private readonly IHostApplicationLifetime _life;
    private readonly ILogger<WindowsShell> _log;
    private Thread? _thread;
    private IntPtr _hwnd, _icon;
    private WndProcDelegate? _wndProc;   // kept alive for the window's lifetime
    private uint _taskbarCreated;
    private string _tip = "";
    private Timer? _tipTimer;
    private int _hotkeysVersion = -1;

    private const int WmDestroy = 0x0002, WmClose = 0x0010, WmCommand = 0x0111, WmHotkey = 0x0312, WmApp = 0x8000, WmTray = WmApp + 1;
    private const int WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const int IdMark = 1, IdSave = 2, IdUi = 3;   // hotkey ids
    private const int MenuOpen = 101, MenuMark = 102, MenuSave = 103, MenuPause = 104, MenuResume = 105, MenuFolder = 106, MenuAutostart = 107, MenuQuit = 108, MenuStatus = 109, MenuStart = 110, MenuStop = 111, MenuDesktop = 112;

    private readonly GameWatcherService _games;

    public WindowsShell(ZamaRecConfig cfg, RecordingCoordinator rec, GameWatcherService games, PauseControl pause, IHostApplicationLifetime life, ILogger<WindowsShell> log)
    {
        _cfg = cfg; _rec = rec; _games = games; _pause = pause; _life = life; _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "zamarec shell" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _tipTimer?.Dispose();
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(3000);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- the message loop

    private void Run()
    {
        try
        {
            _wndProc = WndProc;
            var cls = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null), lpszClassName = "ZamaRECShell",
            };
            if (RegisterClassEx(ref cls) == 0) { _log.LogWarning("tray: RegisterClassEx failed ({err})", Marshal.GetLastWin32Error()); return; }
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _hwnd = CreateWindowEx(0, "ZamaRECShell", "ZamaREC", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { _log.LogWarning("tray: CreateWindowEx failed ({err})", Marshal.GetLastWin32Error()); return; }
            _icon = LoadTrayIcon();
            AddIcon();
            RegisterHotkeys();
            _tipTimer = new Timer(_ => { try { UpdateTip(); RegisterHotkeys(); } catch { } }, null, 3000, 3000);
            _log.LogInformation("tray icon up; hotkeys: mark {mark}, save {save}, ui {ui}", _cfg.Hotkeys.Mark, _cfg.Hotkeys.Save, _cfg.Hotkeys.Ui);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        }
        catch (Exception ex) { _log.LogError(ex, "tray thread failed"); }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WmHotkey:
                    OnHotkey((int)wParam); return IntPtr.Zero;
                case WmTray:
                    switch ((int)lParam & 0xFFFF)
                    {
                        case WmLButtonDblClk: OpenUi(); break;
                        case WmRButtonUp: case WmContextMenu: ShowMenu(); break;
                    }
                    return IntPtr.Zero;
                case WmCommand:
                    OnMenu((int)wParam & 0xFFFF); return IntPtr.Zero;
                case WmClose:
                    DestroyWindow(hwnd); return IntPtr.Zero;
                case WmDestroy:
                    RemoveIcon(); PostQuitMessage(0); return IntPtr.Zero;
            }
            if (msg == _taskbarCreated) { AddIcon(); return IntPtr.Zero; }   // Explorer restarted: put the icon back
        }
        catch (Exception ex) { _log.LogError(ex, "tray message {msg}", msg); }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---------------------------------------------------------------- hotkeys

    private readonly Dictionary<int, string> _registered = new();

    private void RegisterHotkeys()
    {
        if (_cfg.Version == _hotkeysVersion || _hwnd == IntPtr.Zero) return;
        if (Thread.CurrentThread != _thread) { PostMessage(_hwnd, WmApp + 2, IntPtr.Zero, IntPtr.Zero); return; }   // must happen on the window's thread
        _hotkeysVersion = _cfg.Version;
        foreach (var id in _registered.Keys.ToList()) { UnregisterHotKey(_hwnd, id); _registered.Remove(id); }
        foreach (var (id, text) in new[] { (IdMark, _cfg.Hotkeys.Mark), (IdSave, _cfg.Hotkeys.Save), (IdUi, _cfg.Hotkeys.Ui) })
        {
            if (Hotkey.TryParse(text) is not { } hk) continue;
            if (RegisterHotKey(_hwnd, id, hk.Modifiers | Hotkey.NoRepeat, hk.VirtualKey)) _registered[id] = hk.Text;
            else _log.LogWarning("hotkey {text} could not be registered (in use by another program?)", hk.Text);
        }
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case IdMark: _ = Task.Run(() => _rec.MarkAsync(null, CancellationToken.None)); break;
            case IdSave: _ = Task.Run(() => _rec.SaveAsync(null, null, CancellationToken.None)); break;
            case IdUi: OpenUi(); break;
        }
    }

    // ---------------------------------------------------------------- menu

    private void ShowMenu()
    {
        var s = _rec.Status();
        var paused = _pause.Current.Mode != "off";
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuOpen, "Open ZamaREC");
        AppendMenu(menu, MfGrayed, MenuStatus, StatusLine(s, paused));
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, s.State == "idle" ? MfGrayed : 0, MenuMark, "Mark" + Suffix(_cfg.Hotkeys.Mark));
        AppendMenu(menu, s.State == "idle" ? MfGrayed : 0, MenuSave, $"Save last {s.SaveSeconds ?? _cfg.Clips.SaveSeconds} s" + Suffix(_cfg.Hotkeys.Save));
        if (s.State == "idle")
        {
            AppendMenu(menu, 0, MenuStart, "Start recording");   // the running game, else the desktop
            AppendMenu(menu, 0, MenuDesktop, "Record the desktop");
        }
        else AppendMenu(menu, 0, MenuStop, "Stop session");
        AppendMenu(menu, 0, paused ? MenuResume : MenuPause, paused ? "Turn recording on" : "Turn recording off");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, 0, MenuFolder, "Open recordings folder");
        AppendMenu(menu, Autostart.IsOn() ? MfChecked : 0, MenuAutostart, "Start with Windows");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, 0, MenuQuit, "Quit ZamaREC");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);   // so the menu closes when the user clicks elsewhere (Microsoft's own recipe)
        TrackPopupMenu(menu, TpmRightButton | TpmBottomAlign, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
    }

    private static string Suffix(string hotkey) => string.IsNullOrWhiteSpace(hotkey) ? "" : $"\t{hotkey}";

    private static string StatusLine(DaemonStatus s, bool paused) => s.State switch
    {
        "recording" => $"Recording {s.Profile} ({s.ElapsedSeconds / 60:0} min, {s.MarkerCount} markers)",
        "recorder-died" => "Recorder stopped unexpectedly",
        _ => paused ? "Idle, automatic recording is off" : "Idle, waiting for a game",
    };

    private void OnMenu(int id)
    {
        switch (id)
        {
            case MenuOpen: OpenUi(); break;
            case MenuMark: _ = Task.Run(() => _rec.MarkAsync(null, CancellationToken.None)); break;
            case MenuSave: _ = Task.Run(() => _rec.SaveAsync(null, null, CancellationToken.None)); break;
            case MenuPause:
                _ = Task.Run(async () => { _pause.Pause("forever", null, _rec.ActiveGamePid, _rec.Status().Profile); if (_rec.IsActive) await _rec.StopAsync("paused", CancellationToken.None); });
                break;
            case MenuResume: _pause.Resume(); break;
            case MenuStart: _ = Task.Run(() => _games.StartByHandAsync(null, CancellationToken.None)); break;
            case MenuDesktop: _ = Task.Run(() => _games.StartByHandAsync(_cfg.DesktopProfile(), CancellationToken.None)); break;
            case MenuStop: _ = Task.Run(() => _rec.StopAsync("manual", CancellationToken.None)); break;
            case MenuFolder:
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _cfg.RootDir) { UseShellExecute = true }); } catch (Exception ex) { _log.LogWarning(ex, "open folder"); }
                break;
            case MenuAutostart: Autostart.Toggle(_log); break;
            case MenuQuit: _life.StopApplication(); break;
        }
    }

    private void OpenUi() => _ = Task.Run(() =>
    {
        var s = _rec.Status();
        var r = UiLauncher.Open(s.UiUrl ?? UiLauncher.DefaultUrl, s.UiWindow);
        if (r is not ("window" or "focused" or "browser")) _log.LogWarning("open ui: {r}", r);
    });

    // ---------------------------------------------------------------- the icon

    private void AddIcon()
    {
        var data = IconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTray; data.hIcon = _icon; data.szTip = _tip = "ZamaREC";
        if (!Shell_NotifyIcon(NimAdd, ref data)) _log.LogWarning("tray: could not add the icon ({err})", Marshal.GetLastWin32Error());
    }

    private void UpdateTip()
    {
        var s = _rec.Status();
        var tip = "ZamaREC: " + StatusLine(s, _pause.Current.Mode != "off");
        if (tip == _tip) return;
        var data = IconData(); data.uFlags = NifTip; data.szTip = _tip = tip.Length > 120 ? tip[..120] : tip;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void RemoveIcon() { var data = IconData(); Shell_NotifyIcon(NimDelete, ref data); }

    private NotifyIconData IconData() => new() { cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = 1 };

    /// <summary>The exe's own icon (packaging/brand, built in as the application icon), or the stock application icon.</summary>
    private IntPtr LoadTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && ExtractIconEx(exe, 0, out var large, out var small, 1) > 0)
            {
                if (small != IntPtr.Zero) { if (large != IntPtr.Zero) DestroyIcon(large); return small; }
                if (large != IntPtr.Zero) return large;
            }
        }
        catch { }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));   // IDI_APPLICATION
    }

    // ---------------------------------------------------------------- start with Windows

    private static class Autostart
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool IsOn()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue("zamarec") is string v && v.Length > 0; } catch { return false; }
        }
        public static void Toggle(ILogger log)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                if (IsOn()) { k.DeleteValue("zamarec", false); log.LogInformation("autostart off"); }
                else if (Environment.ProcessPath is { } exe) { k.SetValue("zamarec", $"\"{exe}\""); log.LogInformation("autostart on: {exe}", exe); }
            }
            catch (Exception ex) { log.LogWarning(ex, "autostart"); }
        }
    }

    // ---------------------------------------------------------------- Win32

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Point pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint MfSeparator = 0x800, MfGrayed = 1, MfChecked = 8, TpmRightButton = 2, TpmBottomAlign = 0x20;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIcon(uint msg, ref NotifyIconData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
