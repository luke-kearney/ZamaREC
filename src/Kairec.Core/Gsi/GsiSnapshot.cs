using System.Text.Json;

namespace Kairec.Core.Gsi;

/// <summary>
/// The handful of fields we read from a CS2 game state payload. Everything is nullable because CS2 only
/// sends the blocks that exist: no map block in the menu, no player block before you join, and so on.
/// </summary>
public sealed record GsiSnapshot
{
    public string? ProviderSteamId { get; init; }
    public long? ProviderTimestamp { get; init; }
    public string? MapName { get; init; }
    public string? MapPhase { get; init; }
    public int? MapRound { get; init; }
    public string? RoundPhase { get; init; }
    public string? RoundWinTeam { get; init; }
    public string? PlayerSteamId { get; init; }
    public string? PlayerName { get; init; }
    public string? PlayerTeam { get; init; }
    public string? PlayerActivity { get; init; }
    public int? Health { get; init; }
    public int? RoundKills { get; init; }
    public int? RoundKillsHs { get; init; }
    public int? MatchKills { get; init; }
    public int? MatchDeaths { get; init; }
    public int? MatchMvps { get; init; }

    /// <summary>True when the player block describes the person running the game, not someone they are spectating.</summary>
    public bool IsOwnPlayer => ProviderSteamId is not null && PlayerSteamId == ProviderSteamId;

    public static GsiSnapshot Parse(JsonElement root)
    {
        JsonElement? Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;
        string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        int? Int(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;
        long? Long(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var i) ? i : null;

        var provider = Get(root, "provider"); var map = Get(root, "map"); var round = Get(root, "round"); var player = Get(root, "player");
        var state = player is { } p ? Get(p, "state") : null; var stats = player is { } p2 ? Get(p2, "match_stats") : null;
        return new GsiSnapshot
        {
            ProviderSteamId = Str(provider is { } pr ? Get(pr, "steamid") : null),
            ProviderTimestamp = Long(provider is { } pr2 ? Get(pr2, "timestamp") : null),
            MapName = Str(map is { } m ? Get(m, "name") : null),
            MapPhase = Str(map is { } m2 ? Get(m2, "phase") : null),
            MapRound = Int(map is { } m3 ? Get(m3, "round") : null),
            RoundPhase = Str(round is { } r ? Get(r, "phase") : null),
            RoundWinTeam = Str(round is { } r2 ? Get(r2, "win_team") : null),
            PlayerSteamId = Str(player is { } p3 ? Get(p3, "steamid") : null),
            PlayerName = Str(player is { } p4 ? Get(p4, "name") : null),
            PlayerTeam = Str(player is { } p5 ? Get(p5, "team") : null),
            PlayerActivity = Str(player is { } p6 ? Get(p6, "activity") : null),
            Health = Int(state is { } s ? Get(s, "health") : null),
            RoundKills = Int(state is { } s2 ? Get(s2, "round_kills") : null),
            RoundKillsHs = Int(state is { } s3 ? Get(s3, "round_killhs") : null),
            MatchKills = Int(stats is { } st ? Get(st, "kills") : null),
            MatchDeaths = Int(stats is { } st2 ? Get(st2, "deaths") : null),
            MatchMvps = Int(stats is { } st3 ? Get(st3, "mvps") : null),
        };
    }

    public static GsiSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }
}
