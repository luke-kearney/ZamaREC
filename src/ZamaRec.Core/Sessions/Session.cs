using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZamaRec.Core.Sessions;

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
    /// <summary>Timeline mode: playlist file name relative to the media directory.</summary>
    [JsonPropertyName("playlist")] public string? Playlist { get; set; }
    /// <summary>Where the segments live when not in the session directory (RAM timelines: a tmpfs path).</summary>
    [JsonPropertyName("media_dir")] public string? MediaDir { get; set; }
    /// <summary>Set once a RAM timeline's segments have been dropped.</summary>
    [JsonPropertyName("footage_gone")] public bool FootageGone { get; set; }
    /// <summary>Footage bytes this session produced (all segments ever written, or the buffer's churn).</summary>
    [JsonPropertyName("recorded_bytes")] public long RecordedBytes { get; set; }
    /// <summary>True for buffer sessions, where the figure is elapsed x bitrate rather than measured.</summary>
    [JsonPropertyName("bytes_estimated")] public bool BytesEstimated { get; set; }
    /// <summary>Timeline mode: total seconds recorded (including footage already purged from the window).</summary>
    [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
    [JsonPropertyName("game_pid")] public int? GamePid { get; set; }
    /// <summary>Names of the audio tracks in file order ("mix" first). Unset on recordings from before tracks were named: one track.</summary>
    [JsonPropertyName("audio_tracks")] public List<string>? AudioTracks { get; set; }
    /// <summary>The Steam app this session recorded, when Steam launched it.</summary>
    [JsonPropertyName("steam_app_id")] public long? SteamAppId { get; set; }
    [JsonPropertyName("clips")] public List<string> Clips { get; set; } = new();

    /// <summary>The wall-clock time of a point on this session's timeline (seconds from its first frame).</summary>
    public DateTimeOffset TimeAt(double seconds)
    {
        var origin = FirstFrameRealtimeUs is long us ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000).ToOffset(StartedAt.Offset)
            : FirstFrameMonoUs is long ff ? StartedAt.AddSeconds((ff - SpawnMonoUs) / 1e6)
            : StartedAt;
        return origin.AddSeconds(seconds);
    }
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
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
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
    /// <summary>mark | save | export | share (a re-encoded copy made to fit a size)</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "save";
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    /// <summary>Seconds into the session timeline where this clip starts, when known.</summary>
    [JsonPropertyName("session_offset")] public double? SessionOffset { get; set; }
    [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
    /// <summary>When the file was written. Retention counts a clip's age from here.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    /// <summary>
    /// When the footage was recorded (its last frame), for clips cut later from older footage. Unset on mark and
    /// save clips, which are written as they happen: created_at is their time. Lists show and sort by this.
    /// </summary>
    [JsonPropertyName("recorded_at")] public DateTimeOffset? RecordedAt { get; set; }
    [JsonPropertyName("starred")] public bool Starred { get; set; }
    /// <summary>Mark clips expire; null means retention's normal rules apply.</summary>
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("markers")] public List<ClipMarker> Markers { get; set; } = new();
    /// <summary>Names of the audio tracks in file order ("mix" first). Unset = one track.</summary>
    [JsonPropertyName("audio_tracks")] public List<string>? AudioTracks { get; set; }
    /// <summary>Set on an export whose mix was remade from the separate tracks: the level each one was given.</summary>
    [JsonPropertyName("mix_levels")] public Dictionary<string, double>? MixLevels { get; set; }
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
