using ZamaRec.Core.Config;
using Microsoft.Extensions.Logging;

namespace ZamaRec.Core.Recording;

/// <summary>
/// One running capture process. The coordinator drives it the same way on both systems: wait until it is
/// ready, stop it, and (buffer mode, Linux only) ask it to dump its replay buffer.
/// </summary>
public interface IRecorder : IAsyncDisposable
{
    int Pid { get; }
    bool HasExited { get; }
    Task Exited { get; }
    string Executable { get; }
    /// <summary>The last error line the recorder printed, if any: why it would not start or why it died.</summary>
    string? LastError => null;
    /// <summary>Waits until the recorder is capturing (its IPC socket exists, or ffmpeg reports frames).</summary>
    Task WaitForReadyAsync(TimeSpan timeout, CancellationToken ct);
    /// <summary>Stop and save (record mode) or stop without saving the buffer. Returns the saved path in record mode.</summary>
    Task<IpcReply> StopAsync(CancellationToken ct);
    Task<IpcReply> SaveReplayAsync(int? seconds, CancellationToken ct);
    /// <summary>Waits for a clean exit, escalating to a signal and then a kill.</summary>
    Task WaitForExitAsync(TimeSpan grace, CancellationToken ct);
}

public static class Recorder
{
    public static string Name => Platform.IsWindows ? "ffmpeg" : "gpu-screen-recorder";

    /// <summary>
    /// Starts the capture for a profile and waits until it is capturing: gpu-screen-recorder on Linux, ffmpeg with
    /// Desktop Duplication on Windows (which may take more than one attempt, see <see cref="FfmpegRecorder.Routes"/>).
    /// </summary>
    public static async Task<IRecorder> StartReadyAsync(ZamaRecConfig cfg, EffectiveProfile profile, string mediaDir, string bufferOutDir, string ipcSocket, ILogger log, TimeSpan readyTimeout, CancellationToken ct)
    {
        if (Platform.IsWindows) return await FfmpegRecorder.StartReadyAsync(cfg, profile, mediaDir, log, readyTimeout, ct);
        var args = GsrArguments.Build(cfg, profile, mediaDir, bufferOutDir, ipcSocket);
        var rec = GsrProcess.Start(args, ipcSocket, log);
        try { await rec.WaitForReadyAsync(readyTimeout, ct); }
        catch { await rec.DisposeAsync(); throw; }
        return rec;
    }
}
