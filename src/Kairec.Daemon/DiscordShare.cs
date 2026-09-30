using System.Net.Http.Headers;
using System.Text.Json;

namespace Kairec.Daemon;

/// <summary>
/// Posts a clip to a Discord channel through a webhook (share.discord_webhook). Only ever on a click in the UI:
/// one multipart upload of the file with a line of text, nothing else is sent and nothing is read back but the
/// message's link.
/// </summary>
public sealed class DiscordShare(ILogger<DiscordShare> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public sealed record Result(bool Ok, string? Url, string? Error);

    public async Task<Result> SendAsync(string webhook, string filePath, string? text, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["content"] = string.IsNullOrWhiteSpace(text) ? null : text.Length > 1900 ? text[..1900] : text });
            form.Add(new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), "payload_json");
            await using var file = File.OpenRead(filePath);
            var part = new StreamContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            form.Add(part, "files[0]", Path.GetFileName(filePath));
            // wait=true makes Discord answer with the message, whose attachment link is what the UI shows
            using var res = await Http.PostAsync(webhook + (webhook.Contains('?') ? "&" : "?") + "wait=true", form, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var why = (int)res.StatusCode switch
                {
                    413 => "Discord refused the file as too large for that channel; lower share.discord_max_mb",
                    401 or 403 or 404 => "Discord did not accept the webhook address; check it in Settings",
                    429 => "Discord is rate limiting the webhook; try again in a moment",
                    _ => $"Discord answered {(int)res.StatusCode}",
                };
                log.LogWarning("discord upload failed: {code} {body}", (int)res.StatusCode, body.Length > 300 ? body[..300] : body);
                return new Result(false, null, why);
            }
            string? url = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("attachments", out var a) && a.GetArrayLength() > 0 && a[0].TryGetProperty("url", out var u)) url = u.GetString();
            }
            catch (JsonException) { }
            return new Result(true, url, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("discord upload failed: {err}", ex.Message);
            return new Result(false, null, "could not reach Discord: " + ex.Message);
        }
    }
}
