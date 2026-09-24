using System.Text.Json;
using System.Text.Json.Serialization;
using Kairec.Core.Config;
using Kairec.Core.Telemetry;

namespace Kairec.Daemon;

/// <summary>
/// The on/off switch for automatic recording. Paused means the watcher will not start sessions:
/// for the rest of the current game ("session"), until a time ("until"), or until told otherwise ("forever").
/// Manual `kairec start` still works while paused. Persisted so a restart does not silently re-enable.
/// </summary>
public sealed class PauseControl
{
    public sealed class State
    {
        /// <summary>off | session | until | forever</summary>
        [JsonPropertyName("mode")] public string Mode { get; set; } = "off";
        [JsonPropertyName("until")] public DateTimeOffset? Until { get; set; }
        [JsonPropertyName("pid")] public int? Pid { get; set; }
        [JsonPropertyName("profile")] public string? Profile { get; set; }
    }

    private readonly object _lock = new();
    private readonly string _path = Path.Combine(Paths.DataDir(), "pause.json");
    private readonly EventLog _events;
    private State _state = new();

    public PauseControl(EventLog events)
    {
        _events = events;
        try { if (File.Exists(_path)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new(); } catch { }
        if (_state.Mode == "session") _state = new();   // a session pause never outlives the daemon
    }

    public State Current { get { lock (_lock) return new State { Mode = _state.Mode, Until = _state.Until, Pid = _state.Pid, Profile = _state.Profile }; } }

    /// <summary>True when the watcher must not auto-start. Also expires timed and session pauses.</summary>
    public bool IsPaused(Func<int, bool> pidAlive)
    {
        lock (_lock)
        {
            switch (_state.Mode)
            {
                case "until" when _state.Until is { } u && DateTimeOffset.Now >= u:
                    _state = new(); Persist(); _events.Write("daemon", "recording resumed: pause timer ended"); return false;
                case "session" when _state.Pid is int pid && !pidAlive(pid):
                    _state = new(); Persist(); _events.Write("daemon", "recording resumed: the game it was paused for has exited"); return false;
                case "off": return false;
                default: return true;
            }
        }
    }

    public string Pause(string mode, double? hours, int? currentPid, string? profile)
    {
        lock (_lock)
        {
            _state = mode switch
            {
                "session" => new State { Mode = currentPid is null ? "forever" : "session", Pid = currentPid, Profile = profile },
                "until" => new State { Mode = "until", Until = DateTimeOffset.Now.AddHours(Math.Max(0.05, hours ?? 2)) },
                _ => new State { Mode = "forever" },
            };
            Persist();
            var msg = Describe(_state);
            _events.Write("daemon", "recording paused " + msg);
            return msg;
        }
    }

    public void Resume()
    {
        lock (_lock) { if (_state.Mode == "off") return; _state = new(); Persist(); _events.Write("daemon", "recording resumed"); }
    }

    public static string Describe(State s) => s.Mode switch
    {
        "session" => "for the rest of this game",
        "until" => $"until {s.Until:HH:mm}",
        "forever" => "until turned back on",
        _ => "",
    };

    private void Persist()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(_state)); } catch { }
    }
}
