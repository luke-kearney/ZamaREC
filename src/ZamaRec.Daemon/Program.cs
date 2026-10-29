using System.Reflection;
using ZamaRec.Core;
using ZamaRec.Core.Config;
using ZamaRec.Core.Feedback;
using ZamaRec.Core.Sessions;
using ZamaRec.Daemon;
using ZamaRec.Protocol;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

// On Windows the daemon is what the user double-clicks. If one is already running, this launch just opens
// the UI (an explicit user action) and leaves; otherwise it becomes the daemon, tray icon and all.
if (Platform.IsWindows && args.Length == 0 && Startup.DaemonAnswers() is { } running)
{
    UiLauncher.Open(running.UiUrl ?? UiLauncher.DefaultUrl, running.UiWindow);
    return;
}

var configPath = args.Length > 0 ? args[0] : Paths.ConfigFile();
var config = ConfigLoader.LoadOrCreate(configPath);
Directory.CreateDirectory(config.SessionsDir);
Directory.CreateDirectory(config.ClipsDir);
ZamaRec.Core.Export.ClipExporter.GpuSource = () => config.Capture.Gpu;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
if (Platform.IsWindows) builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(Paths.DataDir(), "daemon.log")));   // no console in the Windows build
else builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    var ip = System.Net.IPAddress.Parse(config.Daemon.Bind);
    k.Listen(ip, config.Daemon.UiPort);
    if (config.Daemon.GsiPort != config.Daemon.UiPort) k.Listen(ip, config.Daemon.GsiPort);
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new ZamaRec.Core.Telemetry.EventLog());
builder.Services.AddSingleton(new ZamaRec.Core.Telemetry.StatsStore());
builder.Services.AddSingleton(sp => new ConfigService(config, configPath, sp.GetRequiredService<ZamaRec.Core.Telemetry.EventLog>(), sp.GetRequiredService<ILogger<ConfigService>>()));
builder.Services.AddSingleton(sp => new SoundFeedback(config.Feedback, sp.GetRequiredService<ILogger<SoundFeedback>>()));
builder.Services.AddSingleton<RecordingCoordinator>();
builder.Services.AddSingleton<GsiService>();
builder.Services.AddSingleton<PauseControl>();
builder.Services.AddSingleton<SessionCatalog>();
builder.Services.AddSingleton<SeenGames>();
builder.Services.AddSingleton<BannerService>();
builder.Services.AddSingleton<StemService>();
builder.Services.AddSingleton<ThumbService>();
builder.Services.AddSingleton<DiscordShare>();
builder.Services.AddSingleton<ShareJobs>();
builder.Services.AddHostedService<GameWatcherService>();
builder.Services.AddSingleton<ControlSocketServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlSocketServer>());
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
if (OperatingSystem.IsWindows()) builder.Services.AddHostedService<WindowsShell>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();
var types = new FileExtensionContentTypeProvider();
types.Mappings[".ts"] = "video/mp2t";
types.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
types.Mappings[".webmanifest"] = "application/manifest+json";
// The UI files are copied next to the daemon on Linux and built into the single-file exe on Windows.
var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
IFileProvider ui = Directory.Exists(wwwroot) ? new PhysicalFileProvider(wwwroot) : new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = ui,
    ContentTypeProvider = types,
    // The UI is tiny and changes with every update: make browsers revalidate instead of serving a stale page.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapWebApi();

app.Services.GetRequiredService<ZamaRec.Core.Telemetry.EventLog>().Write("daemon", "daemon started");
app.Services.GetRequiredService<GsiService>().EnsureConfigFile();
var log = app.Services.GetRequiredService<ILogger<Program>>();
// The release version CI builds with (e.g. 0.1.3, or 0.1.0-dev locally), plus the commit it came from.
var appVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
log.LogInformation("zamarec {version} daemon starting; config {path}; ui http://{bind}:{port}; profiles: {profiles}",
    appVersion, configPath, config.Daemon.Bind, config.Daemon.UiPort, string.Join(", ", config.Profiles.Select(p => $"{p.Name} ({p.Mode})")));
{
    var found = ZamaRec.Core.Recording.GpuVendor.Detected;
    var use = ZamaRec.Core.Recording.HwCodec.Normalize(config.Capture.Gpu);
    if (config.Capture.Gpu.Trim().ToLowerInvariant() is var set && set != "auto" && set != use)
        log.LogWarning("capture.gpu is {set} but this machine has {found}: using the {use} encoder", set, string.Join(", ", found), use);
    else log.LogInformation("gpu encoder: {use} (capture.gpu = {set}; found: {found})", use, set, found.Count == 0 ? "nothing" : string.Join(", ", found));
}
var ffmpegVersion = Platform.IsWindows ? ZamaRec.Core.Recording.Ffmpeg.Version() : null;
if (Platform.IsWindows && ffmpegVersion is not null)
    log.LogInformation("zamarec {version} on {os}; {ffmpeg} ({exe})", appVersion, System.Runtime.InteropServices.RuntimeInformation.OSDescription, ffmpegVersion, ZamaRec.Core.Recording.Ffmpeg.Exe);
if (Platform.IsWindows && ffmpegVersion is null)
{
    log.LogError("ffmpeg.exe was not found next to the daemon or on PATH: nothing can be recorded or exported until it is");
    if (OperatingSystem.IsWindows())
        WindowsShell.MessageBox(IntPtr.Zero, "ffmpeg.exe and ffprobe.exe were not found.\n\nPut them in the same folder as zamarec-daemon.exe (the bundle ships them) or on PATH, then start ZamaREC again.\n\nZamaREC stays open so you can browse old recordings.", "ZamaREC: ffmpeg missing", 0x30);
}
try { app.Run(); }
catch (Exception ex)
{
    log.LogCritical(ex, "daemon failed");
    if (OperatingSystem.IsWindows()) WindowsShell.MessageBox(IntPtr.Zero, "ZamaREC could not start:\n\n" + ex.Message + "\n\nSee daemon.log in " + Paths.DataDir(), "ZamaREC", 0x10);
    throw;
}

static class Startup
{
    /// <summary>Asks a running daemon for its status over the control socket; null when none answers quickly.</summary>
    public static DaemonStatus? DaemonAnswers()
    {
        try
        {
            var path = ControlPaths.SocketPath();
            if (!File.Exists(path)) return null;
            using var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            sock.ReceiveTimeout = 2000; sock.SendTimeout = 2000;
            sock.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            sock.Send(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new ControlRequest { Cmd = "status" }, ProtocolJson.Default.ControlRequest) + "\n"));
            var buf = new byte[65536]; var n = 0;
            while (n < buf.Length) { var r = sock.Receive(buf, n, buf.Length - n, System.Net.Sockets.SocketFlags.None); if (r <= 0) break; n += r; if (Array.IndexOf(buf, (byte)'\n', 0, n) >= 0) break; }
            var line = System.Text.Encoding.UTF8.GetString(buf, 0, n).Split('\n')[0];
            return System.Text.Json.JsonSerializer.Deserialize(line, ProtocolJson.Default.ControlResponse)?.Status;
        }
        catch { return null; }
    }
}
