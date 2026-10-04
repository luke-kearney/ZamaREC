using System.Collections.Concurrent;
using ZamaRec.Core.Config;
using ZamaRec.Core.Sessions;
using ZamaRec.Core.Share;
using ZamaRec.Core.Telemetry;

namespace ZamaRec.Daemon;

/// <summary>
/// Sending a clip out, in the background: to the Discord webhook or through a share module ([[share.targets]]).
/// A clip over the target's size limit is re-encoded to fit first, and that smaller copy is kept as a clip.
/// The page starts a job on a click, then asks how it is going; an upload can take minutes and can be cancelled.
/// </summary>
public sealed class ShareJobs(ZamaRecConfig cfg, SessionCatalog cat, RecordingCoordinator rec, DiscordShare discord, EventLog events, ILogger<ShareJobs> log)
{
    public const string Discord = "discord";

    public sealed class Job
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public required string Target { get; init; }
        /// <summary>encoding | sending | done | failed | cancelled</summary>
        public string Stage { get; set; } = "sending";
        /// <summary>0..1 of the upload when the module reports it.</summary>
        public double? Progress { get; set; }
        public string? Note { get; set; }
        public string? Url { get; set; }
        public string? Error { get; set; }
        /// <summary>The file that was sent: the clip, or the copy made to fit.</summary>
        public string? File { get; set; }
        public long Bytes { get; set; }
        public bool Reencoded { get; set; }
        internal CancellationTokenSource Cancel { get; } = new();
        internal DateTimeOffset? Ended { get; set; }
    }

    public sealed record Target(string Id, string Name, double MaxMb, bool Ready, string? Problem);

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    /// <summary>What the Share menu offers: Discord first, then the modules in config order.</summary>
    public IEnumerable<Target> Targets()
    {
        var hook = !string.IsNullOrWhiteSpace(cfg.Share.DiscordWebhook);
        yield return new Target(Discord, "Discord", cfg.Share.DiscordMaxMb, hook, hook ? null : "add a webhook under Settings → Sharing first");
        foreach (var t in cfg.Share.Targets)
        {
            var (path, error) = ShareModule.Resolve(t);
            yield return new Target(t.Name, t.Name, t.MaxMb, path is not null, error);
        }
    }

    public Job? Find(string id) => _jobs.GetValueOrDefault(id);

    public bool CancelJob(string id)
    {
        if (Find(id) is not { Ended: null } job) return false;
        job.Cancel.Cancel();
        return true;
    }

    /// <summary>Starts sending a clip; null with the reason when the target cannot take it.</summary>
    public (Job? Job, string? Error) Start(string targetId, string clipPath, string profile, string? message)
    {
        foreach (var old in _jobs.Values.Where(j => j.Ended is { } at && at < DateTimeOffset.Now.AddMinutes(-30))) _jobs.TryRemove(old.Id, out _);

        var module = cfg.Share.FindTarget(targetId);
        var isDiscord = module is null && string.Equals(targetId, Discord, StringComparison.OrdinalIgnoreCase);
        if (module is null && !isDiscord) return (null, $"no share target is called '{targetId}'");
        var webhook = cfg.Share.DiscordWebhook;
        if (isDiscord && string.IsNullOrWhiteSpace(webhook)) return (null, "no Discord webhook is set: add one under Settings → Sharing");
        if (module is not null && ShareModule.Resolve(module).Error is { } problem) return (null, problem);

        var name = isDiscord ? "Discord" : module!.Name;
        var maxMb = isDiscord ? cfg.Share.DiscordMaxMb : module!.MaxMb;
        var job = new Job { Target = name };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, name, maxMb, clipPath, profile, message, ct => isDiscord ? SendToDiscordAsync(webhook!, job, message, ct) : SendToModuleAsync(module!, job, clipPath, profile, message, ct)));
        return (job, null);
    }

    private async Task RunAsync(Job job, string name, double maxMb, string clipPath, string profile, string? message, Func<CancellationToken, Task<ShareResult>> send)
    {
        var ct = job.Cancel.Token;
        var src = WebApi.ReadClipInfo(clipPath, profile);
        try
        {
            job.File = clipPath;
            if (WebApi.ShareBytes(maxMb) is long cap && new FileInfo(clipPath).Length > cap)
            {
                job.Stage = "encoding";
                var (copy, error) = await WebApi.ShareCopyAsync(clipPath, profile, src, cap, $"{maxMb:0.#}mb", cat, cfg, rec, log, ct);
                if (copy is null) { Finish(job, "failed", error); return; }
                job.File = copy; job.Reencoded = true;
            }
            job.Bytes = new FileInfo(job.File).Length;
            job.Stage = "sending";
            var sent = await send(ct);
            job.Url = sent.Url;
            Finish(job, sent.Ok ? "done" : "failed", sent.Error);
            events.Write(sent.Ok ? "export" : "error", sent.Ok ? $"sent to {name}: {job.Bytes / 1e6:0.0} MB" + (job.Reencoded ? " (re-encoded to fit)" : "") : $"sending to {name} failed: {sent.Error}",
                profile, src.SessionId, Path.GetFileName(job.File), job.Bytes, level: sent.Ok ? "info" : "error");
        }
        catch (OperationCanceledException) { Finish(job, "cancelled", null); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "share to {name} failed", name);
            Finish(job, "failed", ex.Message);
        }
    }

    private static void Finish(Job job, string stage, string? error)
    {
        job.Error = error; job.Stage = stage; job.Ended = DateTimeOffset.Now;
    }

    private async Task<ShareResult> SendToDiscordAsync(string webhook, Job job, string? message, CancellationToken ct)
    {
        var r = await discord.SendAsync(webhook, job.File!, message, ct);
        return new ShareResult(r.Ok, r.Url, r.Error);
    }

    private async Task<ShareResult> SendToModuleAsync(ShareTargetConfig module, Job job, string clipPath, string profile, string? message, CancellationToken ct)
    {
        var info = WebApi.ReadClipInfo(clipPath, profile);
        var request = new ShareRequest
        {
            File = job.File!, FileName = Path.GetFileName(job.File!), Bytes = job.Bytes,
            Title = string.IsNullOrWhiteSpace(message) ? info.Label : message, Profile = profile,
            // a copy made to fit has the clip's length and markers
            DurationSeconds = info.DurationSeconds ?? await ZamaRec.Core.Recording.VideoProbe.DurationSecondsAsync(clipPath, ct), RecordedAt = cat.FootageTime(info), Reencoded = job.Reencoded, Markers = info.Markers,
        };
        return await ShareModule.RunAsync(module, request, (progress, note) => { if (progress is not null) job.Progress = progress; if (note is not null) job.Note = note; }, log, ct);
    }
}
