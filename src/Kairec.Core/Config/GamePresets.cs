namespace Kairec.Core.Config;

/// <summary>A known game: a display name, a default process hook and a banner. A profile points at one with `game = "id"`.</summary>
public sealed record GamePreset(string Id, string Name, string? Match, string? MatchCmdline, string Events, string Mode, string? BannerUrl);

public static class GamePresets
{
    public static readonly IReadOnlyList<GamePreset> All =
    [
        new("cs2", "Counter-Strike 2", "^cs2$", null, "cs2-gsi", "timeline",
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/header.jpg"),
        new("minecraft", "Minecraft", null, "net\\.minecraft|minecraft-launcher|fabric|forge|prismlauncher", "none", "buffer",
            "https://www.minecraft.net/content/dam/minecraftnet/games/minecraft/key-art/Vanilla-PMP_Collection-Carousel-0_Tricky-Trials_1280x768.jpg"),
        new("black-ops-3", "Black Ops III", null, "BlackOps3\\.exe", "none", "timeline",
            "https://cdn.cloudflare.steamstatic.com/steam/apps/311210/header.jpg"),
    ];

    public static GamePreset? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset for a profile: its `game` key, or a name that happens to match a preset id or name (pre-existing configs).</summary>
    public static GamePreset? Resolve(ProfileConfig? p)
    {
        if (p is null) return null;
        return Find(p.Game)
            ?? All.FirstOrDefault(g => string.Equals(g.Id, p.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(g.Name, p.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static GamePreset? ResolveByProfileName(KairecConfig cfg, string profileName) =>
        Resolve(cfg.Profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase)))
        ?? All.FirstOrDefault(g => string.Equals(g.Id, profileName, StringComparison.OrdinalIgnoreCase) || string.Equals(g.Name, profileName, StringComparison.OrdinalIgnoreCase));
}
