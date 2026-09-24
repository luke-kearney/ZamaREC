using Kairec.Core.Config;

namespace Kairec.Daemon;

/// <summary>
/// Game banners for the web UI, cached under &lt;root&gt;/banners. A banner is downloaded once, on first request, so the
/// page never talks to the internet itself and works offline afterwards. A failed download is not retried for an hour.
/// </summary>
public sealed class BannerService(KairecConfig cfg, ILogger<BannerService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, DateTime> _failed = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Dir => Path.Combine(cfg.RootDir, "banners");

    public async Task<string?> PathAsync(string id, CancellationToken ct)
    {
        var preset = GamePresets.Find(id);
        if (preset?.BannerUrl is null) return null;
        var path = Path.Combine(Dir, preset.Id + ".jpg");
        if (File.Exists(path)) return path;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            if (_failed.TryGetValue(preset.Id, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) return null;
            try
            {
                Directory.CreateDirectory(Dir);
                using var req = new HttpRequestMessage(HttpMethod.Get, preset.BannerUrl);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 kairec");
                using var res = await Http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 1024) throw new InvalidOperationException("response too small to be an image");
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, path, overwrite: true);
                log.LogInformation("banner for {game} cached ({bytes} bytes)", preset.Id, bytes.Length);
                return path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failed[preset.Id] = DateTime.UtcNow;
                log.LogWarning("banner for {game} could not be fetched: {err}", preset.Id, ex.Message);
                return null;
            }
        }
        finally { _gate.Release(); }
    }
}
