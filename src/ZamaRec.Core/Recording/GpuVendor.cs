using Microsoft.Win32;

namespace ZamaRec.Core.Recording;

/// <summary>
/// Which GPU makers this machine has, read once from what the system already keeps about its display adapters:
/// the PCI vendor ids in sysfs on Linux, the display adapter class in the registry on Windows. Neither asks the
/// driver or the GPU anything, and nothing here runs again after the first call.
/// </summary>
public static class GpuVendor
{
    private static readonly Lazy<IReadOnlyList<string>> Found = new(() => Platform.IsWindows ? FromRegistry() : FromSysfs());

    /// <summary>The vendors present, as capture.gpu spells them, the preferred encoder first.</summary>
    public static IReadOnlyList<string> Detected => Found.Value;

    /// <summary>
    /// The vendor to use for a configured capture.gpu: "auto" is the detected one, and a vendor this machine does
    /// not have gives way to one it has (a config written with the old "nvidia" default on an AMD machine).
    /// With nothing detected the configured value stands, NVIDIA for "auto".
    /// </summary>
    public static string Resolve(string? configured, IReadOnlyList<string> detected)
    {
        var want = (configured ?? "").Trim().ToLowerInvariant();
        if (want is not ("nvidia" or "amd" or "intel")) want = "auto";
        if (detected.Count == 0) return want == "auto" ? "nvidia" : want;
        return want != "auto" && detected.Contains(want) ? want : detected[0];
    }

    /// <summary>From PCI vendor ids. A discrete card's encoder is preferred over an integrated one's.</summary>
    public static IReadOnlyList<string> FromIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>();
        foreach (var id in ids)
        {
            var v = id.ToLowerInvariant();
            if (v.Contains("10de")) seen.Add("nvidia");
            else if (v.Contains("1002") || v.Contains("1022")) seen.Add("amd");
            else if (v.Contains("8086")) seen.Add("intel");
        }
        return new[] { "nvidia", "amd", "intel" }.Where(seen.Contains).ToList();
    }

    /// <summary>The vendor id behind an adapter's name, for an adapter entry that carries no hardware id.</summary>
    public static string? FromName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("nvidia") || n.Contains("geforce") ? "10de"
            : n.Contains("radeon") || n.Contains("advanced micro devices") || n.Contains("amd") ? "1002"
            : n.Contains("intel") ? "8086" : null;
    }

    private static IReadOnlyList<string> FromSysfs()
    {
        try
        {
            if (!Directory.Exists("/sys/class/drm")) return [];
            return FromIds(Directory.GetDirectories("/sys/class/drm", "card*")
                .Select(d => Path.Combine(d, "device", "vendor")).Where(File.Exists).Select(f => File.ReadAllText(f).Trim()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IReadOnlyList<string> FromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            // The display adapter device class: one numbered subkey per adapter, MatchingDeviceId "pci\ven_1002&dev_...".
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return [];
            var ids = new List<string>();
            foreach (var name in cls.GetSubKeyNames())
            {
                try
                {
                    using var k = cls.OpenSubKey(name);
                    if (k is null) continue;
                    if (k.GetValue("MatchingDeviceId") is string id && id.IndexOf("ven_", StringComparison.OrdinalIgnoreCase) is >= 0 and var i && id.Length >= i + 8) ids.Add(id.Substring(i + 4, 4));
                    else if (FromName($"{k.GetValue("DriverDesc")} {k.GetValue("ProviderName")}") is { } byName) ids.Add(byName);
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }   // "Properties" and the like
            }
            return FromIds(ids);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }
}
