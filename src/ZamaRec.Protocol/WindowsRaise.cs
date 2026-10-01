using System.Runtime.InteropServices;
using System.Text;

namespace ZamaRec.Protocol;

/// <summary>Brings an open top-level window with the given title to the front (Windows). Only ever on an explicit user action.</summary>
public static class WindowsRaise
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    /// <summary>A visible Chromium window titled exactly <paramref name="title"/> (an --app window takes the page's title).</summary>
    public static bool Raise(string title)
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(256);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            sb.Clear(); GetClassName(h, sb, sb.Capacity);
            if (!sb.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
            sb.Clear(); GetWindowText(h, sb, sb.Capacity);
            if (!sb.ToString().Equals(title, StringComparison.Ordinal)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        if (IsIconic(found)) ShowWindow(found, 9);   // SW_RESTORE
        return SetForegroundWindow(found);
    }
}
