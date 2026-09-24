using System.Text.Json;
using System.Text.Json.Serialization;
using Tomlyn;

namespace Kairec.Core.Config;

public sealed class KairecConfig
{
    public StorageConfig Storage { get; set; } = new();
    public CaptureConfig Capture { get; set; } = new();
    public TimelineConfig Timeline { get; set; } = new();
    public BufferConfig Buffer { get; set; } = new();
    public ClipConfig Clips { get; set; } = new();
    public RetentionConfig Retention { get; set; } = new();
    public FeedbackConfig Feedback { get; set; } = new();
    public DaemonConfig Daemon { get; set; } = new();
    public List<ProfileConfig> Profiles { get; set; } = new();

    [JsonIgnore] public string RootDir => Paths.Expand(Storage.Root);
    [JsonIgnore] public string SessionsDir => Path.Combine(RootDir, "sessions");
    [JsonIgnore] public string ClipsDir => Path.Combine(RootDir, "clips");

    /// <summary>Bumped on every reload so long-lived components can notice a change.</summary>
    [JsonIgnore] public int Version { get; private set; }

    /// <summary>Replaces this instance's contents in place, so everyone holding a reference sees the new config.</summary>
    public void CopyFrom(KairecConfig o)
    {
        Storage = o.Storage; Capture = o.Capture; Timeline = o.Timeline; Buffer = o.Buffer; Clips = o.Clips;
        Retention = o.Retention; Feedback = o.Feedback; Daemon = o.Daemon; Profiles = o.Profiles;
        Version++;
    }

    public ProfileConfig? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Effective settings for a profile: profile overrides on top of the global defaults.</summary>
    public EffectiveProfile Effective(ProfileConfig p) => new()
    {
        Profile = p,
        Mode = p.CaptureMode,
        Fps = p.Fps ?? Capture.Fps,
        WindowMinutes = p.Timeline?.WindowMinutes ?? Timeline.WindowMinutes,
        SegmentSeconds = p.Timeline?.SegmentSeconds ?? Timeline.SegmentSeconds,
        BufferMinutes = p.Buffer?.Minutes ?? Buffer.Minutes,
        BufferStorage = p.Buffer?.Storage ?? Buffer.Storage,
        SaveSeconds = p.Clips?.SaveSeconds ?? Clips.SaveSeconds,
        MarkBeforeSeconds = p.Clips?.MarkBeforeSeconds ?? Clips.MarkBeforeSeconds,
        MarkAfterSeconds = p.Clips?.MarkAfterSeconds ?? Clips.MarkAfterSeconds,
        MarkClipExpireHours = p.Clips?.MarkClipExpireHours ?? Clips.MarkClipExpireHours,
        MaxGb = p.Retention?.MaxGb ?? Retention.MaxGb,
        MaxDays = p.Retention?.MaxDays ?? Retention.MaxDays,
    };
}

public sealed class EffectiveProfile
{
    public required ProfileConfig Profile { get; init; }
    public string Name => Profile.Name;
    public required CaptureMode Mode { get; init; }
    public required int Fps { get; init; }
    public required int WindowMinutes { get; init; }
    public required int SegmentSeconds { get; init; }
    public required int BufferMinutes { get; init; }
    public required string BufferStorage { get; init; }
    public required int SaveSeconds { get; init; }
    public required int MarkBeforeSeconds { get; init; }
    public required int MarkAfterSeconds { get; init; }
    public required int MarkClipExpireHours { get; init; }
    public required double? MaxGb { get; init; }
    public required int? MaxDays { get; init; }
}

public sealed class StorageConfig
{
    public string Root { get; set; } = "~/Videos/kairec";
}

public sealed class CaptureConfig
{
    /// <summary>Monitor name as printed by `gpu-screen-recorder --list-monitors`, or "screen" for the first one.</summary>
    public string Monitor { get; set; } = "screen";
    public int Fps { get; set; } = 60;
    /// <summary>h264 | hevc | av1. h264 is the only one every browser plays.</summary>
    public string Codec { get; set; } = "h264";
    /// <summary>Constant bitrate in kbps. CBR keeps the RAM buffer a predictable size.</summary>
    public int BitrateKbps { get; set; } = 20000;
    public double KeyframeSeconds { get; set; } = 1.0;
    /// <summary>gpu-screen-recorder audio sources: default_output, default_input, device:NAME, app:NAME.</summary>
    public List<string> Audio { get; set; } = new() { "default_output" };
    public bool Cursor { get; set; } = false;
}

/// <summary>Timeline mode: a rolling window of segments on disk. Anything older than the window is deleted while you play.</summary>
public sealed class TimelineConfig
{
    /// <summary>0 keeps everything.</summary>
    public int WindowMinutes { get; set; } = 120;
    public int SegmentSeconds { get; set; } = 5;
}

/// <summary>Buffer mode: a ring buffer in RAM (or on disk). Nothing is written until you mark or save.</summary>
public sealed class BufferConfig
{
    public int Minutes { get; set; } = 30;
    /// <summary>ram | disk</summary>
    public string Storage { get; set; } = "ram";
}

public sealed class ClipConfig
{
    /// <summary>How much `kairec save` saves when --last is not given.</summary>
    public int SaveSeconds { get; set; } = 30;
    /// <summary>Buffer mode: a mark dumps this much before the mark...</summary>
    public int MarkBeforeSeconds { get; set; } = 1500;
    /// <summary>...and this much after it, once that much time has passed.</summary>
    public int MarkAfterSeconds { get; set; } = 300;
    /// <summary>Clips produced by a mark are deleted after this many hours unless starred. 0 = never.</summary>
    public int MarkClipExpireHours { get; set; } = 30;
}

public sealed class RetentionConfig
{
    public double? MaxGb { get; set; } = 100;
    public int? MaxDays { get; set; } = 30;
}

public sealed class FeedbackConfig
{
    public bool Sound { get; set; } = true;
    public double Volume { get; set; } = 0.5;
}

public sealed class DaemonConfig
{
    public int GsiPort { get; set; } = 3555;
    public int UiPort { get; set; } = 3556;
    public string Bind { get; set; } = "127.0.0.1";
    /// <summary>How often the daemon scans /proc while no game is running. CPU only; never touches the GPU.</summary>
    public double ProcessScanSeconds { get; set; } = 1.0;
}

public sealed class ProfileConfig
{
    public string Name { get; set; } = "";
    /// <summary>Regex against the process name (/proc/PID/comm, max 15 chars).</summary>
    public string? Match { get; set; }
    /// <summary>Regex against the full command line. Needed for Java games like Minecraft.</summary>
    public string? MatchCmdline { get; set; }
    /// <summary>timeline | buffer</summary>
    public string Mode { get; set; } = "timeline";
    /// <summary>Event source: none | cs2-gsi</summary>
    public string Events { get; set; } = "none";
    public int? Fps { get; set; }
    public ProfileTimelineOverride? Timeline { get; set; }
    public ProfileBufferOverride? Buffer { get; set; }
    public ProfileClipOverride? Clips { get; set; }
    public ProfileRetentionOverride? Retention { get; set; }

    [JsonIgnore] public CaptureMode CaptureMode => Mode.ToLowerInvariant() switch
    {
        "timeline" => CaptureMode.Timeline,
        "buffer" => CaptureMode.Buffer,
        _ => throw new InvalidOperationException($"Profile '{Name}': unknown mode '{Mode}' (timeline | buffer)")
    };
}

public sealed class ProfileTimelineOverride { public int? WindowMinutes { get; set; } public int? SegmentSeconds { get; set; } }
public sealed class ProfileBufferOverride { public int? Minutes { get; set; } public string? Storage { get; set; } }
public sealed class ProfileClipOverride { public int? SaveSeconds { get; set; } public int? MarkBeforeSeconds { get; set; } public int? MarkAfterSeconds { get; set; } public int? MarkClipExpireHours { get; set; } }
public sealed class ProfileRetentionOverride { public double? MaxGb { get; set; } public int? MaxDays { get; set; } }

public enum CaptureMode { Timeline, Buffer }

public static class Paths
{
    public static string ConfigDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Home(), ".config") : xdg;
        return Path.Combine(baseDir, "kairec");
    }

    public static string ConfigFile() => Path.Combine(ConfigDir(), "config.toml");

    public static string DataDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Home(), ".local", "share") : xdg;
        return Path.Combine(baseDir, "kairec");
    }

    public static string Home() => Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Expand(string p) =>
        p.StartsWith("~/") ? Path.Combine(Home(), p[2..]) : p == "~" ? Home() : p;
}

public static class ConfigLoader
{
    private static readonly TomlSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Renders a config back to TOML. Comments from the original file are not preserved.</summary>
    public static string Serialize(KairecConfig cfg) =>
        "# kairec configuration (written by the settings UI; see config.example.toml in the repo for a commented version)\n\n"
        + TomlSerializer.Serialize(cfg, Options);

    public static KairecConfig Parse(string toml)
    {
        var cfg = TomlSerializer.Deserialize<KairecConfig>(toml, Options) ?? throw new InvalidOperationException("empty config");
        Validate(cfg);
        return cfg;
    }

    /// <summary>Loads the config file, writing the default one first if it does not exist.</summary>
    public static KairecConfig LoadOrCreate(string? path = null)
    {
        path ??= Paths.ConfigFile();
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultConfig.Toml);
        }
        return Parse(File.ReadAllText(path));
    }

    public static void Validate(KairecConfig cfg)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cfg.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidOperationException("Every [[profiles]] entry needs a name");
            if (!names.Add(p.Name)) throw new InvalidOperationException($"Duplicate profile name '{p.Name}'");
            if (p.Match is null && p.MatchCmdline is null) throw new InvalidOperationException($"Profile '{p.Name}' needs match or match_cmdline");
            var e = cfg.Effective(p);
            if (e.BufferStorage is not ("ram" or "disk")) throw new InvalidOperationException($"Profile '{p.Name}': buffer storage must be ram or disk");
            if (e.SegmentSeconds < 2) throw new InvalidOperationException($"Profile '{p.Name}': timeline segment_seconds must be at least 2");
            if (e.BufferMinutes < 1) throw new InvalidOperationException($"Profile '{p.Name}': buffer minutes must be at least 1");
        }
        if (cfg.Capture.Codec is not ("h264" or "hevc" or "av1"))
            throw new InvalidOperationException($"capture.codec must be h264, hevc or av1 (got '{cfg.Capture.Codec}')");
    }
}
