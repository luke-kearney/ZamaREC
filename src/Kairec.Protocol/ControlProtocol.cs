using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairec.Protocol;

/// <summary>Newline-delimited JSON over the daemon's Unix socket. One request, one reply.</summary>
public sealed class ControlRequest
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("seconds")] public int? Seconds { get; set; }
    [JsonPropertyName("profile")] public string? Profile { get; set; }
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
    [JsonPropertyName("recorded_seconds")] public double? RecordedSeconds { get; set; }
    [JsonPropertyName("window_seconds")] public double? WindowSeconds { get; set; }
    [JsonPropertyName("pending_marks")] public int PendingMarks { get; set; }
    [JsonPropertyName("profiles")] public List<string> Profiles { get; set; } = new();
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
public partial class ProtocolJson : JsonSerializerContext { }

public static class ControlPaths
{
    public static string SocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime)) runtime = Path.GetTempPath();
        return Path.Combine(runtime, "kairec.sock");
    }
}
