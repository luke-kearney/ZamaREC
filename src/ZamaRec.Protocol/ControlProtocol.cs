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
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
public partial class ProtocolJson : JsonSerializerContext { }

public static class ControlPaths
{
    /// <summary>Control socket. ZAMAREC_SOCKET overrides it so a test daemon never touches the real one.</summary>
    public static string SocketPath()
    {
        var env = Environment.GetEnvironmentVariable("ZAMAREC_SOCKET");
        if (!string.IsNullOrEmpty(env)) return env;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime)) runtime = Path.GetTempPath();
        return Path.Combine(runtime, "zamarec.sock");
    }
}
