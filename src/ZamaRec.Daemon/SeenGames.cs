using System.Text.Json;
using System.Text.Json.Serialization;
using ZamaRec.Core.Config;

namespace ZamaRec.Daemon;

/// <summary>
/// Every Steam game the watcher has seen launched, kept in games.json: app id, name, when. The Games page lists
/// them, and a game without a profile gets its banner through the app id remembered here.
/// </summary>
public sealed class SeenGames
{
    public sealed class Entry
    {
        [JsonPropertyName("app_id")] public long AppId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("first_seen")] public DateTimeOffset FirstSeen { get; set; }
        [JsonPropertyName("last_seen")] public DateTimeOffset LastSeen { get; set; }
        [JsonPropertyName("launches")] public int Launches { get; set; }
        /// <summary>The profile whose process match recorded this app, learned the first time that happens.</summary>
        [JsonPropertyName("profile")] public string? Profile { get; set; }
    }

    private readonly object _lock = new();
    private readonly string _path = Path.Combine(Paths.DataDir(), "games.json");
    private readonly List<Entry> _entries = new();

    public SeenGames()
    {
        try { if (File.Exists(_path)) _entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? new(); } catch { }
    }

    public List<Entry> All { get { lock (_lock) return _entries.OrderByDescending(e => e.LastSeen).ToList(); } }

    public Entry? Find(long appId) { lock (_lock) return _entries.FirstOrDefault(e => e.AppId == appId); }

    public Entry? ByName(string name) { lock (_lock) return _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)); }

    public Entry? ByProfile(string profile) { lock (_lock) return _entries.FirstOrDefault(e => string.Equals(e.Profile, profile, StringComparison.OrdinalIgnoreCase)); }

    /// <summary>Records one launch of the app.</summary>
    public void Touch(long appId, string name) => Change(appId, name, e => { e.Launches++; e.LastSeen = DateTimeOffset.Now; });

    /// <summary>Remembers that this app is the game a profile matches by process, so its launches are never taken for an unconfirmed game.</summary>
    public void Link(long appId, string name, string profile)
    {
        if (Find(appId)?.Profile != profile) Change(appId, name, e => e.Profile = profile);
    }

    private void Change(long appId, string name, Action<Entry> change)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            var e = _entries.FirstOrDefault(x => x.AppId == appId);
            if (e is null) _entries.Add(e = new Entry { AppId = appId, FirstSeen = now, LastSeen = now });
            e.Name = name; change(e);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
                File.Move(tmp, _path, overwrite: true);
            }
            catch { }
        }
    }
}
