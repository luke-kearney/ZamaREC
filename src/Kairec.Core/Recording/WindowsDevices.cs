using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Kairec.Core.Recording;

/// <summary>Monitors and audio devices on Windows, for the settings page. Queried on demand only.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsDevices
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

    private const int AttachedToDesktop = 1, PrimaryDevice = 4, CurrentSettings = -1;

    /// <summary>Attached displays in adapter order, which is the order ddagrab's output_idx counts them on the first GPU.</summary>
    public static List<GsrInfo.Device> Monitors()
    {
        var list = new List<GsrInfo.Device>();
        var idx = 0;
        for (uint i = 0; ; i++)
        {
            var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
            var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            var size = EnumDisplaySettings(dd.DeviceName, CurrentSettings, ref dm) ? $" {dm.dmPelsWidth}x{dm.dmPelsHeight} {dm.dmDisplayFrequency} Hz" : "";
            list.Add(new GsrInfo.Device(idx.ToString(), $"{dd.DeviceString}{size}{((dd.StateFlags & PrimaryDevice) != 0 ? ", primary" : "")}"));
            idx++;
        }
        return list;
    }

    public static List<GsrInfo.Device> AudioDevices()
    {
        var list = new List<GsrInfo.Device>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var defOut = en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName : "";
            var defIn = en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).FriendlyName : "";
            list.Add(new GsrInfo.Device("default_output", "what you hear" + (defOut.Length > 0 ? $" ({defOut})" : "")));
            list.Add(new GsrInfo.Device("default_input", "microphone" + (defIn.Length > 0 ? $" ({defIn})" : "")));
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
                list.Add(new GsrInfo.Device("device:" + d.FriendlyName, d.DataFlow == DataFlow.Render ? "output: " + d.FriendlyName : "input: " + d.FriendlyName));
        }
        catch { }
        return list;
    }
}
