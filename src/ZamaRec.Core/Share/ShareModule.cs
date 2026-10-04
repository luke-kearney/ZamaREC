using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZamaRec.Core.Config;
using ZamaRec.Core.Sessions;

namespace ZamaRec.Core.Share;

/// <summary>What a share module is told about the clip it is to send: one JSON object on its stdin (docs/share-modules.md).</summary>
public sealed class ShareRequest
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>The file to send, absolute. The module reads it and leaves it alone.</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = "video/mp4";
    /// <summary>The clip's name in the library, when it has one.</summary>
    [JsonPropertyName("title")] public string? Title { get; set; }
    /// <summary>The profile it was recorded under: the game.</summary>
    [JsonPropertyName("profile")] public string Profile { get; set; } = "";
    [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
    /// <summary>When the footage was recorded.</summary>
    [JsonPropertyName("recorded_at")] public DateTimeOffset? RecordedAt { get; set; }
    /// <summary>True when this file is a copy re-encoded to fit the target's max_mb.</summary>
    [JsonPropertyName("reencoded")] public bool Reencoded { get; set; }
    /// <summary>The clip's markers, t in seconds from its start.</summary>
    [JsonPropertyName("markers")] public List<ClipMarker> Markers { get; set; } = new();
    /// <summary>The target's settings table from the config, as written there.</summary>
    [JsonPropertyName("settings")] public Dictionary<string, string> Settings { get; set; } = new();
}

/// <summary>How a share ended: a link to what was posted when the service gives one, or why it failed.</summary>
public sealed record ShareResult(bool Ok, string? Url, string? Error);

/// <summary>
/// Runs a share module: a program outside zamarec that uploads a clip. It gets a <see cref="ShareRequest"/> on
/// stdin and answers on stdout, one JSON object per line: any number of {"progress": 0..1, "note": "..."} and
/// then one {"ok": true, "url": "..."} or {"ok": false, "error": "..."}. Its stderr goes to the daemon log.
/// Only ever started by a click in the UI.
/// </summary>
public static class ShareModule
{
    private static readonly JsonSerializerOptions RequestJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The module's path, or null with the reason when it cannot be run.</summary>
    public static (string? Path, string? Error) Resolve(ShareTargetConfig target)
    {
        var path = Paths.Expand(target.Command.Trim());
        if (!File.Exists(path)) return (null, $"the module of '{target.Name}' was not found: {path}");
        return (path, null);
    }

    public static async Task<ShareResult> RunAsync(ShareTargetConfig target, ShareRequest request, Action<double?, string?> onProgress, ILogger log, CancellationToken ct)
    {
        var (path, missing) = Resolve(target);
        if (path is null) return new ShareResult(false, null, missing);
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        foreach (var a in target.Args) psi.ArgumentList.Add(a);
        request.Settings = target.Settings;

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("no process"); }
        catch (Exception ex) { return new ShareResult(false, null, $"could not start the module of '{target.Name}': {ex.Message}"); }
        using var _ = proc;
        // a cancel kills the module and whatever it started; nothing of the upload is left for zamarec to tidy
        await using var kill = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        ShareResult? result = null;
        string? lastError = null;
        var stderr = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lastError = line.Trim();
                log.LogInformation("share module {name}: {line}", target.Name, line.Length > 500 ? line[..500] : line);
            }
        });
        try
        {
            // the settings can hold a token, so they travel on stdin: never in the arguments or the environment, which other processes can read
            await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(request, RequestJson));
            proc.StandardInput.Close();
        }
        catch (IOException) { }   // it exited without reading; the exit code below says why
        while (await proc.StandardOutput.ReadLineAsync() is { } line)
        {
            if (ParseLine(line) is not { } msg) continue;
            if (msg.Result is not null) result = msg.Result;
            else onProgress(msg.Progress, msg.Note);
        }
        await stderr;
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (result is not null) return result;
        var why = proc.ExitCode == 0 ? "it ended without saying whether the clip was sent" : $"it exited with code {proc.ExitCode}" + (lastError is null ? "" : ": " + lastError);
        return new ShareResult(false, null, $"the module of '{target.Name}' failed: {why}");
    }

    public readonly record struct Line(double? Progress, string? Note, ShareResult? Result);

    /// <summary>One line of a module's stdout. Anything that is not a JSON object is ignored, so a module may print freely.</summary>
    public static Line? ParseLine(string line)
    {
        line = line.Trim();
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            string? Str(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (o.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return ok.GetBoolean()
                    ? new Line(null, null, new ShareResult(true, Str("url"), null))
                    : new Line(null, null, new ShareResult(false, null, Str("error") ?? "the module gave no reason"));
            double? progress = o.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? Math.Clamp(p.GetDouble(), 0, 1) : null;
            var note = Str("note");
            return progress is null && note is null ? null : new Line(progress, note, null);
        }
        catch (JsonException) { return null; }
    }
}
