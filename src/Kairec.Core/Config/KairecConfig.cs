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
    public GsiConfig Gsi { get; set; } = new();
    /// <summary>Named quality presets a profile can pick; blank profile.quality means the [capture] values.</summary>
    public List<QualityPreset> Quality { get; set; } = QualityPreset.Defaults();
    public List<ProfileConfig> Profiles { get; set; } = new();

    public QualityPreset? FindQuality(string? name) =>
        string.IsNullOrEmpty(name) ? null : Quality.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore] public string RootDir => Paths.Expand(Storage.Root);
    [JsonIgnore] public string SessionsDir => Path.Combine(RootDir, "sessions");
    [JsonIgnore] public string ClipsDir => Path.Combine(RootDir, "clips");

    /// <summary>Bumped on every reload so long-lived components can notice a change.</summary>
    [JsonIgnore] public int Version { get; private set; }

    /// <summary>Replaces this instance's contents in place, so everyone holding a reference sees the new config.</summary>
    public void CopyFrom(KairecConfig o)
    {
        Storage = o.Storage; Capture = o.Capture; Timeline = o.Timeline; Buffer = o.Buffer; Clips = o.Clips;
        Retention = o.Retention; Feedback = o.Feedback; Daemon = o.Daemon; Gsi = o.Gsi; Quality = o.Quality; Profiles = o.Profiles;
        Version++;
    }

    public ProfileConfig? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Effective settings for a profile: profile overrides on top of the global defaults.</summary>
    public EffectiveProfile Effective(ProfileConfig p)
    {
        var q = FindQuality(p.Quality) ?? FindQuality(Capture.Quality);
        return new()
        {
        Profile = p,
        Mode = p.CaptureMode,
        QualityName = q?.Name,
        Fps = p.Fps ?? q?.Fps ?? Capture.Fps,
        Codec = q?.Codec ?? Capture.Codec,
        BitrateKbps = q?.BitrateKbps ?? Capture.BitrateKbps,
        KeyframeSeconds = q?.KeyframeSeconds ?? Capture.KeyframeSeconds,
        Scale = q?.Scale ?? Capture.Scale,
        WindowMinutes = p.Timeline?.WindowMinutes ?? Timeline.WindowMinutes,
        SegmentSeconds = p.Timeline?.SegmentSeconds ?? Timeline.SegmentSeconds,
        TimelineStorage = p.Timeline?.Storage ?? Timeline.Storage,
        RamKeepHours = p.Timeline?.RamKeepHours ?? Timeline.RamKeepHours,
        BufferMinutes = p.Buffer?.Minutes ?? Buffer.Minutes,
        BufferStorage = p.Buffer?.Storage ?? Buffer.Storage,
        SaveSeconds = p.Clips?.SaveSeconds ?? Clips.SaveSeconds,
        MarkBeforeSeconds = p.Clips?.MarkBeforeSeconds ?? Clips.MarkBeforeSeconds,
        MarkAfterSeconds = p.Clips?.MarkAfterSeconds ?? Clips.MarkAfterSeconds,
        MarkClipExpireHours = p.Clips?.MarkClipExpireHours ?? Clips.MarkClipExpireHours,
        MarkSaves = p.CaptureMode == CaptureMode.Buffer || (p.Clips?.MarkSaves ?? Clips.MarkSaves ?? ((p.Timeline?.Storage ?? Timeline.Storage) == "ram")),
        EventClips = p.Clips?.EventClips ?? Clips.EventClips,
        MaxGb = p.Retention?.MaxGb ?? Retention.MaxGb,
        MaxDays = p.Retention?.MaxDays ?? Retention.MaxDays,
        };
    }
}

/// <summary>A named bundle of encoder settings. Null fields fall back to [capture].</summary>
public sealed class QualityPreset
{
    public string Name { get; set; } = "";
    public int? Fps { get; set; }
    public string? Codec { get; set; }
    public int? BitrateKbps { get; set; }
    public double? KeyframeSeconds { get; set; }
    /// <summary>Output size limit like "1280x720"; empty = native.</summary>
    public string? Scale { get; set; }
    public string? Note { get; set; }

    public static List<QualityPreset> Defaults() => new()
    {
        new() { Name = "low", Fps = 30, Codec = "h264", BitrateKbps = 6000, Scale = "1280x720", Note = "720p30, ~2.7 GB/h" },
        new() { Name = "medium", Fps = 60, Codec = "h264", BitrateKbps = 12000, Note = "1080p60, ~5.4 GB/h" },
        new() { Name = "high", Fps = 60, Codec = "h264", BitrateKbps = 20000, Note = "1080p60, ~9 GB/h" },
        new() { Name = "ultra", Fps = 60, Codec = "h264", BitrateKbps = 35000, Note = "1080p60, ~16 GB/h" },
    };
}

public sealed class EffectiveProfile
{
    public required ProfileConfig Profile { get; init; }
    public string Name => Profile.Name;
    public required CaptureMode Mode { get; init; }
    public string? QualityName { get; init; }
    public required int Fps { get; init; }
    public required string Codec { get; init; }
    public required int BitrateKbps { get; init; }
    public required double KeyframeSeconds { get; init; }
    public string? Scale { get; init; }
    public required int WindowMinutes { get; init; }
    public required int SegmentSeconds { get; init; }
    public required string TimelineStorage { get; init; }
    public required double RamKeepHours { get; init; }
    /// <summary>Bytes the window needs at this profile's bitrate (video only; audio adds a few percent).</summary>
    public long WindowBytes() => WindowMinutes <= 0 ? 0 : (long)WindowMinutes * 60 * BitrateKbps * 1000 / 8;
    public required int BufferMinutes { get; init; }
    public required string BufferStorage { get; init; }
    public required int SaveSeconds { get; init; }
    public required int MarkBeforeSeconds { get; init; }
    public required int MarkAfterSeconds { get; init; }
    public required int MarkClipExpireHours { get; init; }
    public required bool MarkSaves { get; init; }
    public required List<string> EventClips { get; init; }

    /// <summary>Does this game event deserve a clip? Matches "type" or "kill:N" entries.</summary>
    public bool EventWantsClip(string type, int killCount)
    {
        foreach (var e in EventClips)
        {
            var parts = e.Split(':');
            if (parts[0] != type) continue;
            if (parts.Length == 1) return true;
            if (int.TryParse(parts[1], out var min) && killCount >= min) return true;
        }
        return false;
    }
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
    /// <summary>Quality preset used by profiles that do not pick their own. Empty = the raw fps/codec/bitrate values below.</summary>
    public string Quality { get; set; } = "high";
    public int Fps { get; set; } = 60;
    /// <summary>h264 | hevc | av1. h264 is the only one every browser plays.</summary>
    public string Codec { get; set; } = "h264";
    /// <summary>Constant bitrate in kbps. CBR keeps the RAM buffer a predictable size.</summary>
    public int BitrateKbps { get; set; } = 20000;
    public double KeyframeSeconds { get; set; } = 1.0;
    /// <summary>Output size limit like "1280x720"; empty = native resolution.</summary>
    public string? Scale { get; set; }
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
    /// <summary>disk | ram. RAM keeps the segments on tmpfs: scrubbable, no disk writes, gone after ram_keep_hours or a reboot.</summary>
    public string Storage { get; set; } = "disk";
    public string RamDir { get; set; } = "/dev/shm/kairec";
    /// <summary>How long a finished RAM session's footage stays before it is dropped. It is also dropped when the next session starts.</summary>
    public double RamKeepHours { get; set; } = 6;
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
    /// <summary>Timeline mode: also write a clip around every mark (buffer mode always does). Null = on for RAM timelines, off for disk.</summary>
    public bool? MarkSaves { get; set; }
    /// <summary>Game events that also write a clip like a manual mark: kill:N (N or more kills in the round), mvp, death, round_win, round_loss.</summary>
    public List<string> EventClips { get; set; } = new() { "kill:2", "mvp" };
}

public sealed class GsiConfig
{
    /// <summary>CS2 cfg folder. Empty = find it through Steam's library list.</summary>
    public string CfgDir { get; set; } = "";
    /// <summary>Append every payload to gsi.jsonl in the session folder (useful for building test fixtures).</summary>
    public bool CaptureRaw { get; set; } = true;
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
    /// <summary>Name of a [[quality]] preset; empty = the [capture] values.</summary>
    public string? Quality { get; set; }
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

public sealed class ProfileTimelineOverride { public int? WindowMinutes { get; set; } public int? SegmentSeconds { get; set; } public string? Storage { get; set; } public double? RamKeepHours { get; set; } }
public sealed class ProfileBufferOverride { public int? Minutes { get; set; } public string? Storage { get; set; } }
public sealed class ProfileClipOverride { public int? SaveSeconds { get; set; } public int? MarkBeforeSeconds { get; set; } public int? MarkAfterSeconds { get; set; } public int? MarkClipExpireHours { get; set; } public bool? MarkSaves { get; set; } public List<string>? EventClips { get; set; } }
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
            if (!string.IsNullOrEmpty(cfg.Capture.Quality) && cfg.FindQuality(cfg.Capture.Quality) is null)
                throw new InvalidOperationException($"capture.quality: unknown preset '{cfg.Capture.Quality}' (known: {string.Join(", ", cfg.Quality.Select(q => q.Name))})");
            if (!string.IsNullOrEmpty(p.Quality) && cfg.FindQuality(p.Quality) is null)
                throw new InvalidOperationException($"Profile '{p.Name}': unknown quality preset '{p.Quality}' (known: {string.Join(", ", cfg.Quality.Select(q => q.Name))})");
            var e = cfg.Effective(p);
            if (e.Codec is not ("h264" or "hevc" or "av1")) throw new InvalidOperationException($"Profile '{p.Name}': codec must be h264, hevc or av1");
            if (e.BufferStorage is not ("ram" or "disk")) throw new InvalidOperationException($"Profile '{p.Name}': buffer storage must be ram or disk");
            if (e.TimelineStorage is not ("disk" or "ram")) throw new InvalidOperationException($"Profile '{p.Name}': timeline storage must be disk or ram");
            if (e.TimelineStorage == "ram" && e.WindowMinutes <= 0) throw new InvalidOperationException($"Profile '{p.Name}': a RAM timeline needs a window (window_minutes > 0)");
            if (e.SegmentSeconds < 2) throw new InvalidOperationException($"Profile '{p.Name}': timeline segment_seconds must be at least 2");
            if (e.BufferMinutes < 1) throw new InvalidOperationException($"Profile '{p.Name}': buffer minutes must be at least 1");
        }
        if (cfg.Capture.Codec is not ("h264" or "hevc" or "av1"))
            throw new InvalidOperationException($"capture.codec must be h264, hevc or av1 (got '{cfg.Capture.Codec}')");
    }
}
