using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairec.Core.Sessions;

public sealed class SessionInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("profile")] public string Profile { get; set; } = "";
    /// <summary>timeline | buffer</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "auto";
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; set; }
    [JsonPropertyName("ended_at")] public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Monotonic microseconds when the daemon spawned the recorder.</summary>
    [JsonPropertyName("spawn_mono_us")] public long SpawnMonoUs { get; set; }
    /// <summary>Monotonic microseconds of the first encoded video frame, from the recorder's .ts file.</summary>
    [JsonPropertyName("first_frame_mono_us")] public long? FirstFrameMonoUs { get; set; }
    [JsonPropertyName("first_frame_realtime_us")] public long? FirstFrameRealtimeUs { get; set; }
    /// <summary>Timeline mode: playlist file name relative to the session directory.</summary>
    [JsonPropertyName("playlist")] public string? Playlist { get; set; }
    /// <summary>Timeline mode: total seconds recorded (including footage already purged from the window).</summary>
    [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
    [JsonPropertyName("game_pid")] public int? GamePid { get; set; }
    [JsonPropertyName("clips")] public List<string> Clips { get; set; } = new();
}

public sealed class Marker
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>manual | save | kill | death | round_win | round_loss | mvp | map_start | map_end ...</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "manual";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("t_mono_us")] public long TMonoUs { get; set; }
    /// <summary>Seconds into the session timeline. Null until the first-frame timestamp is known.</summary>
    [JsonPropertyName("t_video")] public double? TVideo { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    [JsonPropertyName("starred")] public bool Starred { get; set; }
    /// <summary>Buffer mode: the clip this mark was written into.</summary>
    [JsonPropertyName("clip")] public string? Clip { get; set; }
    [JsonPropertyName("data")] public Dictionary<string, JsonElement>? Data { get; set; }
}

public sealed class MarkerFile
{
    [JsonPropertyName("markers")] public List<Marker> Markers { get; set; } = new();
}

/// <summary>One timeline segment the daemon has seen in the recorder's playlist.</summary>
public sealed class SegmentEntry
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    /// <summary>Seconds from the first frame of the session to the start of this segment.</summary>
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
}

public sealed class SegmentFile
{
    [JsonPropertyName("segments")] public List<SegmentEntry> Segments { get; set; } = new();
}

/// <summary>Sidecar written next to every clip in clips/. Clips carry their own markers so they can be re-cut later.</summary>
public sealed class ClipInfo
{
    [JsonPropertyName("profile")] public string Profile { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    /// <summary>mark | save | export</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "save";
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    /// <summary>Seconds into the session timeline where this clip starts, when known.</summary>
    [JsonPropertyName("session_offset")] public double? SessionOffset { get; set; }
    [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("starred")] public bool Starred { get; set; }
    /// <summary>Mark clips expire; null means retention's normal rules apply.</summary>
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("markers")] public List<ClipMarker> Markers { get; set; } = new();
}

public sealed class ClipMarker
{
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "manual";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("data")] public Dictionary<string, JsonElement>? Data { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(MarkerFile))]
[JsonSerializable(typeof(SegmentFile))]
[JsonSerializable(typeof(ClipInfo))]
public partial class SessionJson : JsonSerializerContext { }
