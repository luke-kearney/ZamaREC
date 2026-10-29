using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZamaRec.Protocol;

/// <summary>Newline-delimited JSON over the daemon's Unix socket. One request, one reply.</summary>
public sealed class ControlRequest
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("seconds")] public int? Seconds { get; set; }
    [JsonPropertyName("profile")] public string? Profile { get; set; }
    /// <summary>pause: session | until | forever</summary>
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("hours")] public double? Hours { get; set; }
    /// <summary>status: also measure storage. That walks the RAM dir, so ask every few seconds, not every second.</summary>
    [JsonPropertyName("storage")] public bool? Storage { get; set; }
}

public sealed class ControlResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("status")] public DaemonStatus? Status { get; set; }
}

public sealed class DaemonStatus
{
    [JsonPropertyName("state")] public string State { get; set; } = "idle";
    [JsonPropertyName("profile")] public string? Profile { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("session_dir")] public string? SessionDir { get; set; }
    [JsonPropertyName("elapsed_seconds")] public double? ElapsedSeconds { get; set; }
    [JsonPropertyName("marker_count")] public int MarkerCount { get; set; }
    [JsonPropertyName("game_pid")] public int? GamePid { get; set; }
    /// <summary>True while recording a Steam game that has no profile.</summary>
    [JsonPropertyName("unconfirmed")] public bool Unconfirmed { get; set; }
    [JsonPropertyName("recorded_seconds")] public double? RecordedSeconds { get; set; }
    [JsonPropertyName("window_seconds")] public double? WindowSeconds { get; set; }
    [JsonPropertyName("pending_marks")] public int PendingMarks { get; set; }
    [JsonPropertyName("daemon_pid")] public int DaemonPid { get; set; }
    [JsonPropertyName("recorder_pid")] public int? RecorderPid { get; set; }
    [JsonPropertyName("root_dir")] public string? RootDir { get; set; }
    [JsonPropertyName("session_started_at")] public DateTimeOffset? SessionStartedAt { get; set; }
    [JsonPropertyName("gsi_last_seconds")] public double? GsiLastSeconds { get; set; }
    [JsonPropertyName("gsi_map")] public string? GsiMap { get; set; }
    /// <summary>Empty when auto recording is on, otherwise a description of the pause.</summary>
    [JsonPropertyName("paused")] public string? Paused { get; set; }
    [JsonPropertyName("ui_url")] public string? UiUrl { get; set; }
    /// <summary>app | browser: how "open the UI" opens it (daemon.ui_window).</summary>
    [JsonPropertyName("ui_window")] public string? UiWindow { get; set; }
    [JsonPropertyName("save_seconds")] public int? SaveSeconds { get; set; }
    [JsonPropertyName("profiles")] public List<string> Profiles { get; set; } = new();
    /// <summary>How long the session keeps: the timeline window or the buffer, in seconds. Null for an unlimited window.</summary>
    [JsonPropertyName("window_limit_seconds")] public double? WindowLimitSeconds { get; set; }
    /// <summary>True when the session's footage is in RAM (tmpfs or the replay buffer).</summary>
    [JsonPropertyName("in_ram")] public bool? InRam { get; set; }
    /// <summary>The newest line of the event log (kept in memory, not read back from the file).</summary>
    [JsonPropertyName("last_event")] public StatusEvent? LastEvent { get; set; }
    /// <summary>What CS2's game state integration last said; null outside a match.</summary>
    [JsonPropertyName("game")] public GameState? Game { get; set; }
    /// <summary>Only when the request asked for it.</summary>
    [JsonPropertyName("storage")] public StorageState? Storage { get; set; }
}

public sealed class StatusEvent
{
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; set; }
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class GameState
{
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    /// <summary>warmup | live | intermission | gameover</summary>
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("round")] public int? Round { get; set; }
    [JsonPropertyName("score_ct")] public int? ScoreCt { get; set; }
    [JsonPropertyName("score_t")] public int? ScoreT { get; set; }
    /// <summary>CT | T, the player's own side.</summary>
    [JsonPropertyName("team")] public string? Team { get; set; }
    [JsonPropertyName("kills")] public int? Kills { get; set; }
    [JsonPropertyName("assists")] public int? Assists { get; set; }
    [JsonPropertyName("deaths")] public int? Deaths { get; set; }
    [JsonPropertyName("mvps")] public int? Mvps { get; set; }
}

public sealed class StorageState
{
    /// <summary>What the RAM dir (timeline.ram_dir) holds, and what its tmpfs has left.</summary>
    [JsonPropertyName("ram_used_bytes")] public long RamUsedBytes { get; set; }
    [JsonPropertyName("ram_free_bytes")] public long RamFreeBytes { get; set; }
    /// <summary>The disk under storage.root.</summary>
    [JsonPropertyName("disk_free_bytes")] public long DiskFreeBytes { get; set; }
    [JsonPropertyName("disk_total_bytes")] public long DiskTotalBytes { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
public partial class ProtocolJson : JsonSerializerContext { }

public static class ControlPaths
{
    /// <summary>Where the daemon keeps its data: XDG_DATA_HOME/zamarec on Linux, local app data on Windows.</summary>
    public static string DataDir()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zamarec");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "zamarec");
    }

    /// <summary>How to start the daemon on this system, for error messages.</summary>
    public static string StartHint => OperatingSystem.IsWindows() ? "start zamarec-daemon.exe" : "systemctl --user start zamarec";

    /// <summary>Control socket. ZAMAREC_SOCKET overrides it so a test daemon never touches the real one.</summary>
    public static string SocketPath()
    {
        var env = Environment.GetEnvironmentVariable("ZAMAREC_SOCKET");
        if (!string.IsNullOrEmpty(env)) return env;
        if (OperatingSystem.IsWindows())   // Unix sockets work on Windows 10+, in the user's local app data
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zamarec", "zamarec.sock");
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime)) runtime = Path.GetTempPath();
        return Path.Combine(runtime, "zamarec.sock");
    }
}
