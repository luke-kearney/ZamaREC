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
        internal Task Done { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// Several clips to one target, sent one at a time: each is an ordinary job, so a module sees nothing new.
    /// Between clips it waits while a game is being recorded, so a re-encode or upload never starts during play.
    /// </summary>
    public sealed class Batch
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public required string Target { get; init; }
        public required List<BatchItem> Items { get; init; }
        /// <summary>running | waiting (a game is being recorded) | done | cancelled</summary>
        public string Stage { get; set; } = "running";
        /// <summary>The job of the clip being sent now.</summary>
        public Job? Current { get; set; }
        internal CancellationTokenSource Cancel { get; } = new();
        internal DateTimeOffset? Ended { get; set; }
    }

    public sealed class BatchItem
    {
        public required string File { get; init; }
        public required string Profile { get; init; }
        public required string Path { get; init; }
        /// <summary>null until its turn, then done | failed | cancelled</summary>
        public string? Stage { get; set; }
        public string? Url { get; set; }
        public string? Error { get; set; }
        public bool Reencoded { get; set; }
    }

    public sealed record Target(string Id, string Name, double MaxMb, bool Ready, string? Problem);

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ConcurrentDictionary<string, Batch> _batches = new();

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
        job.Done = Task.Run(() => RunAsync(job, name, maxMb, clipPath, profile, message, ct => isDiscord ? SendToDiscordAsync(webhook!, job, message, ct) : SendToModuleAsync(module!, job, clipPath, profile, message, ct)));
        return (job, null);
    }

    public Batch? FindBatch(string id) => _batches.GetValueOrDefault(id);

    /// <summary>Cancels the clip being sent and every one still waiting; those already sent stay sent.</summary>
    public bool CancelBatch(string id)
    {
        if (FindBatch(id) is not { Ended: null } batch) return false;
        batch.Cancel.Cancel();
        if (batch.Current is { } job) CancelJob(job.Id);
        return true;
    }

    /// <summary>Starts sending several clips (path, profile) to one target, in turn; null with the reason when the target cannot take them.</summary>
    public (Batch? Batch, string? Error) StartBatch(string targetId, IReadOnlyList<(string Path, string Profile)> clips)
    {
        foreach (var old in _batches.Values.Where(b => b.Ended is { } at && at < DateTimeOffset.Now.AddMinutes(-30))) _batches.TryRemove(old.Id, out _);
        if (clips.Count == 0) return (null, "no clips to send");
        if (Targets().FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase)) is not { } target) return (null, $"no share target is called '{targetId}'");
        if (!target.Ready) return (null, target.Problem);
        var batch = new Batch
        {
            Target = target.Name,
            Items = clips.Select(c => new BatchItem { File = System.IO.Path.GetFileName(c.Path), Profile = c.Profile, Path = c.Path }).ToList(),
        };
        _batches[batch.Id] = batch;
        _ = Task.Run(() => RunBatchAsync(batch, target.Id));
        return (batch, null);
    }

    private async Task RunBatchAsync(Batch batch, string targetId)
    {
        var ct = batch.Cancel.Token;
        try
        {
            foreach (var item in batch.Items)
            {
                // a game being recorded comes first: the next re-encode or upload waits until it ends (a desktop recording does not count)
                while (rec.ActiveSession?.Info.Profile is { } playing && !string.Equals(playing, DesktopConfig.ProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    batch.Stage = "waiting";
                    await Task.Delay(2000, ct);
                }
                batch.Stage = "running";
                ct.ThrowIfCancellationRequested();
                var (job, error) = Start(targetId, item.Path, item.Profile, WebApi.ReadClipInfo(item.Path, item.Profile).Label);
                if (job is null) { item.Stage = "failed"; item.Error = error; continue; }
                batch.Current = job;
                await job.Done;
                item.Stage = job.Stage; item.Url = job.Url; item.Error = job.Error; item.Reencoded = job.Reencoded;
                ct.ThrowIfCancellationRequested();
            }
            batch.Stage = "done";
        }
        catch (OperationCanceledException) { batch.Stage = "cancelled"; }
        catch (Exception ex) { log.LogWarning(ex, "sending several clips to {target} failed", batch.Target); batch.Stage = "done"; }
        foreach (var item in batch.Items.Where(i => i.Stage is null)) item.Stage = "cancelled";
        batch.Current = null;
        batch.Ended = DateTimeOffset.Now;
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
        var game = cfg.FindProfile(profile);
        var request = new ShareRequest
        {
            SteamAppId = game?.SteamAppId ?? (game is null ? null : GamePresets.Resolve(game)?.SteamAppId),
            File = job.File!, FileName = Path.GetFileName(job.File!), Bytes = job.Bytes,
            Title = string.IsNullOrWhiteSpace(message) ? info.Label : message, Profile = profile,
            // a copy made to fit has the clip's length and markers
            DurationSeconds = info.DurationSeconds ?? await ZamaRec.Core.Recording.VideoProbe.DurationSecondsAsync(clipPath, ct), RecordedAt = cat.FootageTime(info), Reencoded = job.Reencoded, Markers = info.Markers,
        };
        return await ShareModule.RunAsync(module, request, (progress, note) => { if (progress is not null) job.Progress = progress; if (note is not null) job.Note = note; }, log, ct);
    }
}
