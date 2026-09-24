using System.Text.Json;
using System.Text.Json.Serialization;
using Kairec.Core.Config;

namespace Kairec.Core.Telemetry;

/// <summary>One line of the append-only event log (events.jsonl in the data dir).</summary>
public sealed class LogEvent
{
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; set; } = DateTimeOffset.Now;
    /// <summary>info | warn | error</summary>
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
    /// <summary>daemon | session | mark | save | clip | export | config | purge | error</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("profile")] public string? Profile { get; set; }
    [JsonPropertyName("session")] public string? Session { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("seconds")] public double? Seconds { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LogEvent))]
[JsonSerializable(typeof(StatsCounters))]
public partial class TelemetryJson : JsonSerializerContext { }

/// <summary>Append-only JSON lines log of what the daemon did. Cheap to write, easy to tail, survives restarts.</summary>
public sealed class EventLog
{
    private readonly object _lock = new();
    public string Path { get; }

    public EventLog(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(Paths.DataDir(), "events.jsonl");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    public LogEvent Write(string kind, string message, string? profile = null, string? session = null, string? file = null, long? bytes = null, double? seconds = null, string level = "info")
    {
        var e = new LogEvent { Kind = kind, Message = message, Profile = profile, Session = session, File = file, Bytes = bytes, Seconds = seconds, Level = level };
        var line = JsonSerializer.Serialize(e, TelemetryJson.Default.LogEvent) + "\n";
        lock (_lock) File.AppendAllText(Path, line);
        return e;
    }

    /// <summary>Last <paramref name="limit"/> events, newest last. Reads the tail of the file only.</summary>
    public List<LogEvent> Tail(int limit = 200, string? kind = null)
    {
        var result = new List<LogEvent>();
        if (!File.Exists(Path)) return result;
        string[] lines;
        lock (_lock)
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = Math.Min(fs.Length, 1L << 20);   // last 1 MB is plenty for a few thousand events
            fs.Seek(-take, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (take < fs.Length && lines.Length > 0) lines = lines[1..];   // first line may be cut
        }
        for (int i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            try
            {
                var e = JsonSerializer.Deserialize(lines[i], TelemetryJson.Default.LogEvent);
                if (e is null || (kind is not null && e.Kind != kind)) continue;
                result.Add(e);
            }
            catch { }
        }
        result.Reverse();
        return result;
    }
}

/// <summary>Lifetime counters, kept in stats.json so they survive sessions being deleted by retention.</summary>
public sealed class StatsCounters
{
    [JsonPropertyName("sessions")] public int Sessions { get; set; }
    [JsonPropertyName("seconds_recorded")] public double SecondsRecorded { get; set; }
    /// <summary>Bytes of footage written to disk by timeline sessions.</summary>
    [JsonPropertyName("disk_bytes")] public long DiskBytes { get; set; }
    /// <summary>Bytes of footage that went to RAM instead of disk (RAM timelines measured, buffer sessions estimated).</summary>
    [JsonPropertyName("ram_bytes")] public long RamBytes { get; set; }
    [JsonPropertyName("clip_bytes")] public long ClipBytes { get; set; }
    [JsonPropertyName("clips")] public int Clips { get; set; }
    [JsonPropertyName("marks")] public int Marks { get; set; }
    [JsonPropertyName("saves")] public int Saves { get; set; }
    [JsonPropertyName("exports")] public int Exports { get; set; }
}

public sealed class StatsStore
{
    private readonly object _lock = new();
    public string Path { get; }
    public StatsCounters Counters { get; }

    public StatsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(Paths.DataDir(), "stats.json");
        Counters = File.Exists(Path) ? JsonSerializer.Deserialize(File.ReadAllText(Path), TelemetryJson.Default.StatsCounters) ?? new() : new();
    }

    public void Update(Action<StatsCounters> change)
    {
        lock (_lock)
        {
            change(Counters);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Counters, TelemetryJson.Default.StatsCounters));
            File.Move(tmp, Path, overwrite: true);
        }
    }
}
