using Kairos.Core.Config;
using Kairos.Core.Feedback;
using Kairos.Daemon;

var builder = Host.CreateApplicationBuilder(args);

var configPath = args.Length > 0 ? args[0] : Paths.ConfigFile();
var config = ConfigLoader.LoadOrCreate(configPath);
Directory.CreateDirectory(config.SessionsDir);
Directory.CreateDirectory(config.ClipsDir);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(sp => new SoundFeedback(config.Feedback, sp.GetRequiredService<ILogger<SoundFeedback>>()));
builder.Services.AddSingleton<RecordingCoordinator>();
builder.Services.AddHostedService<GameWatcherService>();
builder.Services.AddHostedService<ControlSocketServer>();

var host = builder.Build();
host.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("kairos daemon starting; config {path}; profiles: {profiles}", configPath, string.Join(", ", config.Profiles.Select(p => $"{p.Name} ({p.Mode})")));
host.Run();
