using Kairec.Core.Config;
using Kairec.Core.Feedback;
using Kairec.Core.Sessions;
using Kairec.Daemon;
using Microsoft.AspNetCore.StaticFiles;

var configPath = args.Length > 0 ? args[0] : Paths.ConfigFile();
var config = ConfigLoader.LoadOrCreate(configPath);
Directory.CreateDirectory(config.SessionsDir);
Directory.CreateDirectory(config.ClipsDir);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    var ip = System.Net.IPAddress.Parse(config.Daemon.Bind);
    k.Listen(ip, config.Daemon.UiPort);
    if (config.Daemon.GsiPort != config.Daemon.UiPort) k.Listen(ip, config.Daemon.GsiPort);
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new Kairec.Core.Telemetry.EventLog());
builder.Services.AddSingleton(new Kairec.Core.Telemetry.StatsStore());
builder.Services.AddSingleton(sp => new ConfigService(config, configPath, sp.GetRequiredService<Kairec.Core.Telemetry.EventLog>(), sp.GetRequiredService<ILogger<ConfigService>>()));
builder.Services.AddSingleton(sp => new SoundFeedback(config.Feedback, sp.GetRequiredService<ILogger<SoundFeedback>>()));
builder.Services.AddSingleton<RecordingCoordinator>();
builder.Services.AddSingleton<GsiService>();
builder.Services.AddSingleton<PauseControl>();
builder.Services.AddSingleton<SessionCatalog>();
builder.Services.AddHostedService<GameWatcherService>();
builder.Services.AddHostedService<ControlSocketServer>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();
var types = new FileExtensionContentTypeProvider();
types.Mappings[".ts"] = "video/mp2t";
types.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = types });
app.MapWebApi();

app.Services.GetRequiredService<Kairec.Core.Telemetry.EventLog>().Write("daemon", "daemon started");
app.Services.GetRequiredService<GsiService>().EnsureConfigFile();
app.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("kairec daemon starting; config {path}; ui http://{bind}:{port}; profiles: {profiles}",
        configPath, config.Daemon.Bind, config.Daemon.UiPort, string.Join(", ", config.Profiles.Select(p => $"{p.Name} ({p.Mode})")));
app.Run();
