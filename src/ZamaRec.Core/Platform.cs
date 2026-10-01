using System.Runtime.InteropServices;

namespace ZamaRec.Core;

/// <summary>
/// Which OS the same build is running on. zamarec is one code base: the Linux build wraps gpu-screen-recorder,
/// the Windows build drives ffmpeg's Desktop Duplication capture. Everything above the recorder is shared.
/// </summary>
public static class Platform
{
    public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static string ExeName(string name) => IsWindows ? name + ".exe" : name;
}
