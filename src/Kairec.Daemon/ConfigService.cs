using Kairec.Core.Config;

namespace Kairec.Daemon;

/// <summary>
/// Owns the config file. Changes are validated, written, and applied in place; if a session is
/// running they wait until it ends, since capture settings cannot change under a live recorder.
/// </summary>
public sealed class ConfigService
{
    private readonly KairecConfig _live;
    private readonly ILogger<ConfigService> _log;
    private readonly object _lock = new();
    private KairecConfig? _pending;

    public string Path { get; }
    public bool HasPending { get { lock (_lock) return _pending is not null; } }

    public ConfigService(KairecConfig live, string path, ILogger<ConfigService> log)
    {
        _live = live; Path = path; _log = log;
    }

    public string ReadText() => File.Exists(Path) ? File.ReadAllText(Path) : DefaultConfig.Toml;

    /// <summary>Validates and saves TOML text. Returns "applied", "pending" or throws with the validation error.</summary>
    public string SaveText(string toml, bool sessionRunning)
    {
        var parsed = ConfigLoader.Parse(toml);   // throws on bad config
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, toml);
        File.Move(tmp, Path, overwrite: true);
        return Apply(parsed, sessionRunning);
    }

    public string SaveStructured(KairecConfig cfg, bool sessionRunning)
    {
        ConfigLoader.Validate(cfg);
        var toml = ConfigLoader.Serialize(cfg);
        var parsed = ConfigLoader.Parse(toml);   // round-trip: what we wrote must read back
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, toml);
        File.Move(tmp, Path, overwrite: true);
        return Apply(parsed, sessionRunning);
    }

    /// <summary>Re-reads the file from disk (for `kairec reload` after a hand edit).</summary>
    public string ReloadFromDisk(bool sessionRunning) => Apply(ConfigLoader.Parse(ReadText()), sessionRunning);

    private string Apply(KairecConfig cfg, bool sessionRunning)
    {
        lock (_lock)
        {
            if (sessionRunning) { _pending = cfg; _log.LogInformation("config saved; applies when the current session ends"); return "pending"; }
            ApplyNow(cfg);
            return "applied";
        }
    }

    /// <summary>Called by the coordinator after a session stops.</summary>
    public void ApplyPendingIfAny()
    {
        lock (_lock)
        {
            if (_pending is null) return;
            ApplyNow(_pending);
            _pending = null;
        }
    }

    private void ApplyNow(KairecConfig cfg)
    {
        var portsChanged = cfg.Daemon.UiPort != _live.Daemon.UiPort || cfg.Daemon.GsiPort != _live.Daemon.GsiPort || cfg.Daemon.Bind != _live.Daemon.Bind;
        _live.CopyFrom(cfg);
        Directory.CreateDirectory(_live.SessionsDir);
        Directory.CreateDirectory(_live.ClipsDir);
        _log.LogInformation("config applied (v{v}); profiles: {profiles}{note}", _live.Version,
            string.Join(", ", _live.Profiles.Select(p => $"{p.Name} ({p.Mode})")), portsChanged ? "; port/bind changes need a daemon restart" : "");
    }
}
