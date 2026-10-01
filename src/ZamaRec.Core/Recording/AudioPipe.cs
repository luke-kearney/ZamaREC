using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ZamaRec.Core.Recording;

/// <summary>
/// One WASAPI capture feeding ffmpeg raw PCM over a named pipe (Windows only). Sources: default_output (what
/// you hear, loopback), default_input (the microphone), device:NAME (a render or capture device by its name).
/// Application sources (app:NAME) are not available on Windows.
///
/// The stream is kept on the wall clock: it opens with silence for the time between the recorder's base instant
/// and the pipe connecting, and is padded or trimmed whenever it falls more than a fifth of a second behind or
/// ahead of the clock, so a device that stops delivering (loopback does when nothing plays) never desyncs it.
/// A silent output on the loopback device keeps it delivering in the first place.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioPipe : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly IWaveIn _capture;
    private readonly WasapiOut? _keepAlive;
    private readonly ILogger _log;
    private readonly long _baseRealUs;
    private readonly int _bytesPerSec;
    private readonly object _lock = new();
    private readonly Timer _pacer;
    private long _written;          // bytes handed to ffmpeg, including silence
    private bool _connected, _closed;
    private DateTime _connectedAt;

    public string Path { get; }
    public string FfmpegFormat { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    private AudioPipe(string name, IWaveIn capture, WasapiOut? keepAlive, long baseRealUs, ILogger log)
    {
        _capture = capture; _keepAlive = keepAlive; _baseRealUs = baseRealUs; _log = log;
        var f = capture.WaveFormat;
        SampleRate = f.SampleRate; Channels = f.Channels;
        FfmpegFormat = f.Encoding == WaveFormatEncoding.IeeeFloat ? "f32le" : f.BitsPerSample switch { 16 => "s16le", 32 => "s32le", 24 => "s24le", _ => "u8" };
        _bytesPerSec = f.AverageBytesPerSecond;
        Path = @"\\.\pipe\" + name;
        _server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
        _keepAlive?.Play();
        _ = AcceptAsync();
        _pacer = new Timer(_ => Pace(), null, 500, 100);
    }

    public static AudioPipe? TryOpen(string source, string pipeName, long baseRealUs, ILogger log)
    {
        try
        {
            var s = source.Trim();
            if (s.Equals("default_output", StringComparison.OrdinalIgnoreCase))
            {
                var cap = new WasapiLoopbackCapture();
                return new AudioPipe(pipeName, cap, Silence(null), baseRealUs, log);
            }
            if (s.Equals("default_input", StringComparison.OrdinalIgnoreCase))
                return new AudioPipe(pipeName, new WasapiCapture(), null, baseRealUs, log);
            if (s.StartsWith("device:", StringComparison.OrdinalIgnoreCase))
            {
                var name = s[7..].Trim();
                using var en = new MMDeviceEnumerator();
                var dev = en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).FirstOrDefault(d => d.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase) || d.ID == name);
                if (dev is null) { log.LogWarning("audio device '{name}' not found; that source is left out", name); return null; }
                return dev.DataFlow == DataFlow.Render
                    ? new AudioPipe(pipeName, new WasapiLoopbackCapture(dev), Silence(dev), baseRealUs, log)
                    : new AudioPipe(pipeName, new WasapiCapture(dev), null, baseRealUs, log);
            }
            log.LogWarning("audio source '{src}' is not available on Windows (default_output, default_input, device:NAME are); left out", s);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "could not open audio source '{src}'; left out", source);
            return null;
        }
    }

    /// <summary>A silent stream on the render device, so its loopback keeps producing samples while nothing else plays.</summary>
    private static WasapiOut Silence(MMDevice? device)
    {
        var o = device is null ? new WasapiOut(AudioClientShareMode.Shared, 200) : new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
        o.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        return o;
    }

    private async Task AcceptAsync()
    {
        try
        {
            await _server.WaitForConnectionAsync();
            lock (_lock)
            {
                _connected = true; _connectedAt = DateTime.UtcNow;
                // Media time 0 is the base instant; this pipe is late by however long ffmpeg took to open it.
                var lateUs = MonotonicClock.RealtimeMicros() - _baseRealUs;
                WriteSilenceLocked(Bytes(lateUs));
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "audio pipe {path}", Path); }
    }

    private long Bytes(long micros) => Align(micros * _bytesPerSec / 1_000_000);
    private long Align(long bytes) { var frame = _capture.WaveFormat.BlockAlign; return bytes - bytes % frame; }
    private long ExpectedLocked() => Bytes(MonotonicClock.RealtimeMicros() - _baseRealUs);

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (!_connected || _closed) return;   // audio captured before ffmpeg opened the pipe belongs before media time 0: dropped
            var expected = ExpectedLocked();
            var lead = _written + e.BytesRecorded - expected;
            var slack = _bytesPerSec / 5;
            var offset = 0;
            if (lead > slack) offset = (int)Math.Min(e.BytesRecorded, Align(lead - slack / 2));   // the device is ahead of the clock: drop the oldest part
            if (e.BytesRecorded - offset > 0) WriteLocked(e.Buffer, offset, e.BytesRecorded - offset);
        }
    }

    private void Pace()
    {
        lock (_lock)
        {
            if (!_connected || _closed) return;
            var behind = ExpectedLocked() - _written;
            if (behind > _bytesPerSec / 5) WriteSilenceLocked(Align(behind - _bytesPerSec / 10));
        }
    }

    private void WriteSilenceLocked(long bytes)
    {
        if (bytes <= 0) return;
        var zero = new byte[Math.Min(bytes, 1 << 16)];
        while (bytes > 0) { var n = (int)Math.Min(bytes, zero.Length); WriteLocked(zero, 0, n); bytes -= n; }
    }

    private void WriteLocked(byte[] buf, int offset, int count)
    {
        try { _server.Write(buf, offset, count); _written += count; }
        catch (Exception ex) { _closed = true; _log.LogDebug(ex, "audio pipe closed"); }
    }

    public void Dispose()
    {
        lock (_lock) _closed = true;
        try { _pacer.Dispose(); } catch { }
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        try { _keepAlive?.Stop(); _keepAlive?.Dispose(); } catch { }
        try { _server.Dispose(); } catch { }
    }
}
