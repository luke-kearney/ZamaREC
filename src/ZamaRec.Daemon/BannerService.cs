using System.Text.Json;
using System.Text.RegularExpressions;
using ZamaRec.Core.Config;

namespace ZamaRec.Daemon;

/// <summary>
/// One banner per profile for the web UI, cached under &lt;root&gt;/banners so the page never talks to the internet.
/// Resolution order: an uploaded image (custom/), the profile's banner_url (url/), its steam_app_id or the preset's
/// (steam/), the app id Steam launched the game with (learned from a session, or an unconfirmed game of that name), then a one-time Steam store
/// search by profile name (search/). Downloads happen once, on first request;
/// a failed download is not retried for an hour, a search that found nothing not for a day.
/// </summary>
public sealed class BannerService(ZamaRecConfig cfg, SeenGames seen, ILogger<BannerService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, DateTime> _failed = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".webp"];

    private string Dir => Path.Combine(cfg.RootDir, "banners");
    private string CustomDir => Path.Combine(Dir, "custom");

    public static string Slug(string profile) => Regex.Replace(profile.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } s ? s : "profile";

    /// <summary>What the profile's banner would come from, for the settings page.</summary>
    public string Source(string profile)
    {
        if (CustomPath(profile) is not null) return "custom";
        var p = cfg.FindProfile(profile); var preset = GamePresets.ResolveByProfileName(cfg, profile);
        if (!string.IsNullOrEmpty(p?.BannerUrl)) return "url";
        if (p?.SteamAppId is not null) return "steam";
        if (preset?.BannerUrl is not null || preset?.SteamAppId is not null) return "preset";
        if ((seen.ByProfile(profile) ?? seen.ByName(profile)) is { } game) return $"steam (launched as app {game.AppId})";
        var s = ReadSearch(profile);
        return s is null ? "search pending" : s.AppId is int id ? $"steam search: {s.Name} ({id})" : "none found";
    }

    public string? CustomPath(string profile)
    {
        if (!Directory.Exists(CustomDir)) return null;
        var slug = Slug(profile);
        return ImageExts.Select(e => Path.Combine(CustomDir, slug + e)).FirstOrDefault(File.Exists);
    }

    public async Task SaveCustomAsync(string profile, Stream body, string contentType, CancellationToken ct)
    {
        var ext = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp",
            _ => throw new InvalidOperationException("send a JPEG, PNG or WebP image")
        };
        Directory.CreateDirectory(CustomDir);
        var slug = Slug(profile);
        var tmp = Path.Combine(CustomDir, slug + ".tmp");
        await using (var f = File.Create(tmp)) await body.CopyToAsync(f, ct);
        if (new FileInfo(tmp).Length < 256) { File.Delete(tmp); throw new InvalidOperationException("that file is too small to be an image"); }
        RemoveCustom(profile);
        File.Move(tmp, Path.Combine(CustomDir, slug + ext), overwrite: true);
        log.LogInformation("custom banner saved for {profile}", profile);
    }

    public bool RemoveCustom(string profile)
    {
        var any = false;
        foreach (var p in ImageExts.Select(e => Path.Combine(CustomDir, Slug(profile) + e)).Where(File.Exists)) { File.Delete(p); any = true; }
        return any;
    }

    public async Task<string?> PathAsync(string profile, CancellationToken ct)
    {
        if (CustomPath(profile) is { } custom) return custom;
        var p = cfg.FindProfile(profile);
        var preset = GamePresets.ResolveByProfileName(cfg, profile);
        var url = p?.BannerUrl;
        var appId = p?.SteamAppId ?? preset?.SteamAppId;
        if (string.IsNullOrEmpty(url) && appId is null) url = preset?.BannerUrl;
        if (string.IsNullOrEmpty(url) && appId is null && (seen.ByProfile(profile) ?? seen.ByName(profile)) is { AppId: <= int.MaxValue } game) appId = (int)game.AppId;

        if (!string.IsNullOrEmpty(url))
            return await FetchAsync(Path.Combine(Dir, "url", $"{Slug(profile)}-{Hash(url)}.jpg"), url, ct);
        if (appId is int id) return await SteamHeaderAsync(id, ct);

        var found = await SearchAsync(profile, ct);
        return found is int fid ? await SteamHeaderAsync(fid, ct) : null;
    }

    /// <summary>Steam header art: the legacy CDN path covers older apps; newer ones only exist under a hashed path that appdetails reveals.</summary>
    private async Task<string?> SteamHeaderAsync(int appId, CancellationToken ct)
    {
        var path = Path.Combine(Dir, "steam", $"{appId}.jpg");
        if (File.Exists(path)) return path;
        if (await FetchAsync(path, GamePresets.SteamHeaderUrl(appId), ct, quiet: true) is { } got) return got;
        string? alt = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://store.steampowered.com/api/appdetails?appids={appId}&filter=basic&cc=US&l=english");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 zamarec");
            using var res = await Http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            foreach (var entry in doc.RootElement.EnumerateObject())   // keyed by an app id that is not always the one asked for
                if (entry.Value.TryGetProperty("data", out var data) && data.TryGetProperty("header_image", out var h)) { alt = h.GetString(); break; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("steam appdetails for {app} failed: {err}", appId, ex.Message); }
        return alt is null ? null : await FetchAsync(path, alt, ct);
    }

    // ---- Steam store search by name, remembered per profile

    private sealed record SearchResult(int? AppId, string? Name, DateTime At);

    private string SearchPath(string profile) => Path.Combine(Dir, "search", Slug(profile) + ".json");

    private SearchResult? ReadSearch(string profile)
    {
        try { var p = SearchPath(profile); return File.Exists(p) ? JsonSerializer.Deserialize<SearchResult>(File.ReadAllText(p)) : null; }
        catch { return null; }
    }

    private async Task<int?> SearchAsync(string profile, CancellationToken ct)
    {
        var cached = ReadSearch(profile);
        if (cached is not null && (cached.AppId is not null || DateTime.UtcNow - cached.At < TimeSpan.FromDays(1))) return cached.AppId;
        await _gate.WaitAsync(ct);
        try
        {
            cached = ReadSearch(profile);
            if (cached is not null && (cached.AppId is not null || DateTime.UtcNow - cached.At < TimeSpan.FromDays(1))) return cached.AppId;
            int? appId = null; string? name = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(profile)}&l=english&cc=US");
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 zamarec");
                using var res = await Http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var first = items[0];
                    appId = first.GetProperty("id").GetInt32(); name = first.GetProperty("name").GetString();
                }
                log.LogInformation("steam search for {profile}: {result}", profile, appId is null ? "nothing" : $"{name} ({appId})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("steam search for {profile} failed: {err}", profile, ex.Message); return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(SearchPath(profile))!);
            await File.WriteAllTextAsync(SearchPath(profile), JsonSerializer.Serialize(new SearchResult(appId, name, DateTime.UtcNow)), ct);
            return appId;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Forget a cached search so the next request looks again (after the profile was renamed, say).</summary>
    public void ForgetSearch(string profile) { var p = SearchPath(profile); if (File.Exists(p)) File.Delete(p); }

    // ---- download once

    private async Task<string?> FetchAsync(string path, string url, CancellationToken ct, bool quiet = false)
    {
        if (File.Exists(path)) return path;
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            if (_failed.TryGetValue(url, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 zamarec");
                using var res = await Http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 1024) throw new InvalidOperationException("response too small to be an image");
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, path, overwrite: true);
                log.LogInformation("banner cached: {file} ({bytes} bytes)", Path.GetFileName(path), bytes.Length);
                return path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failed[url] = DateTime.UtcNow;
                if (!quiet) log.LogWarning("banner {url} could not be fetched: {err}", url, ex.Message);
                return null;
            }
        }
        finally { _gate.Release(); }
    }

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
}
