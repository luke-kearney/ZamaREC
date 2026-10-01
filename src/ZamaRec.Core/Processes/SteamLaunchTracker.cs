using ZamaRec.Core.Config;

namespace ZamaRec.Core.Processes;

/// <summary>What to record for a Steam launch: a configured profile, or (Profile null) the defaults for an unconfirmed game.</summary>
public sealed record LaunchDecision(SteamLaunch Launch, ProfileConfig? Profile, string Name);

/// <summary>
/// Decides, scan by scan, which Steam launch deserves a session when no profile's process matched.
///   - A launch whose app id belongs to a profile starts that profile, unless the profile has a process match
///     of its own: then its process is what starts it and the launch is left alone.
///     The same goes for an app a process-matched profile has recorded before (claimed).
///   - Any other launch is an unconfirmed game. It waits out a grace period first, because the process a
///     profile matches can appear well after Steam's wrapper does.
///   - Launches that were running when a session ended are stale: a launcher that lingers after the game, or
///     a game you stopped by hand, must not start a new session.
/// </summary>
public sealed class SteamLaunchTracker
{
    private readonly Dictionary<int, long> _firstSeenMs = new();
    private readonly HashSet<int> _stale = new();

    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Launches seen for the first time in the last Decide call, for the "seen games" list.</summary>
    public List<SteamLaunch> New { get; } = new();

    public LaunchDecision? Decide(IReadOnlyList<SteamLaunch> launches, ZamaRecConfig cfg, long nowMs, Func<long, string?> nameOf, Func<long, bool>? claimed = null)
    {
        var alive = launches.Select(l => l.Pid).ToHashSet();
        foreach (var pid in _firstSeenMs.Keys.Where(p => !alive.Contains(p)).ToList()) _firstSeenMs.Remove(pid);
        _stale.IntersectWith(alive);
        New.Clear();

        LaunchDecision? decision = null;
        foreach (var l in launches)
        {
            if (_firstSeenMs.TryAdd(l.Pid, nowMs)) New.Add(l);
            if (decision is not null || _stale.Contains(l.Pid)) continue;

            if (cfg.ProfileForSteamApp(l.AppId) is { } profile)
            {
                if (profile.Match is null && profile.MatchCmdline is null) decision = new LaunchDecision(l, profile, profile.Name);
                continue;
            }
            if (claimed?.Invoke(l.AppId) == true) continue;  // known to be the game some profile matches by process
            if (!cfg.AutoGames.Enabled || cfg.AutoGames.Ignores(l.AppId) || l.AppId > int.MaxValue) continue;
            if (nowMs - _firstSeenMs[l.Pid] < Grace.TotalMilliseconds) continue;
            var name = nameOf(l.AppId);
            if (name is null) continue;                      // not an installed Steam app (a non-Steam shortcut, say)
            if (SteamLibrary.IsToolName(name)) continue;
            if (cfg.FindProfile(name) is not null) continue; // a profile of that name exists: its own match decides
            decision = new LaunchDecision(l, null, name);
        }
        return decision;
    }

    public void MarkStale(IEnumerable<SteamLaunch> launches) { foreach (var l in launches) _stale.Add(l.Pid); }
    public void MarkStale(int pid) => _stale.Add(pid);
    public void ClearStale() => _stale.Clear();
}
