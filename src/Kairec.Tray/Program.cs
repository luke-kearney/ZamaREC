using System.Diagnostics;
using Kairec.Protocol;
using Kairec.Tray;
using Tmds.DBus.Protocol;

// kairec-tray: a StatusNotifierItem for Plasma. Shows the daemon state, offers quick actions.
// It never opens a window of its own accord; "open web UI" opens or raises the UI window on request (UiLauncher).

var conn = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("no session bus"));
await conn.ConnectAsync();

var sni = new StatusNotifierItem();
var iconDir = Icons.WriteThemeDir();
if (iconDir is not null) { sni.IconThemePath = iconDir; sni.IconName = Icons.IdleName; }
var menu = new DbusMenu();
conn.AddMethodHandlers([sni, menu]);

var busName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
await conn.RequestNameAsync(busName, RequestNameOptions.None);

string? uiUrl = null; string? uiWindow = null; string? rootDir = null;
void Open(string target) { try { Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })?.Dispose(); } catch { } }
void Cmd(string cmd, string? mode = null, double? hours = null, int? seconds = null) =>
    _ = Task.Run(() => DaemonClient.Send(new ControlRequest { Cmd = cmd, Mode = mode, Hours = hours, Seconds = seconds }));

void OpenUi() { if (uiUrl is { } url) { var mode = uiWindow; _ = Task.Run(() => UiLauncher.Open(url, mode)); } }
sni.OnActivate = OpenUi;
sni.OnSecondaryActivate = () => Cmd("mark");

MessageBuffer RegisterMessage()
{
    using var w = conn.GetMessageWriter();
    w.WriteMethodCallHeader("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem", "s");
    w.WriteString(busName);
    return w.CreateMessage();
}
MessageBuffer RegisteredItemsMessage()
{
    using var w = conn.GetMessageWriter();
    w.WriteMethodCallHeader("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.freedesktop.DBus.Properties", "Get", "ss");
    w.WriteString("org.kde.StatusNotifierWatcher"); w.WriteString("RegisteredStatusNotifierItems");
    return w.CreateMessage();
}
async Task RegisterAsync()
{
    try { await conn.CallMethodAsync(RegisterMessage()); }
    catch (Exception ex) { Console.Error.WriteLine("StatusNotifierWatcher not available yet: " + ex.Message); }
}
if (Environment.GetEnvironmentVariable("KAIREC_TRAY_NOREG") is null) await RegisterAsync();   // NOREG: probe over busctl without Plasma

// Re-register when plasmashell (the host) comes back, e.g. after a Plasma restart.
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        try
        {
            var items = await conn.CallMethodAsync(RegisteredItemsMessage(), (Message msg, object? _) => msg.GetBodyReader().ReadVariantValue().GetArray<string>(), null);
            if (!items.Any(i => i.StartsWith(busName) || i.StartsWith(conn.UniqueName ?? "\0"))) await RegisterAsync();
        }
        catch { }
    }
});

string lastKey = "";
while (true)
{
    var st = DaemonClient.Status();
    uiUrl = st?.UiUrl ?? uiUrl; uiWindow = st?.UiWindow ?? uiWindow; rootDir = st?.RootDir ?? rootDir;
    var recording = st?.State == "recording";
    var paused = st?.Paused is not null;
    var key = st is null ? "gone" : $"{st.State}|{st.Profile}|{st.Paused}|{st.MarkerCount}";
    if (key != lastKey)
    {
        lastKey = key;
        sni.Icon = st is null ? Icons.Gone : recording ? Icons.Recording : paused ? Icons.Paused : Icons.Idle;
        if (iconDir is not null) sni.IconName = st is null ? Icons.GoneName : recording ? Icons.RecordingName : paused ? Icons.PausedName : Icons.IdleName;
        // Idle is the only state with nothing to tell; everything else keeps the icon on the panel.
        var status = st is not null && !recording && !paused ? "Passive" : "Active";
        if (status != sni.Status) { sni.Status = status; sni.EmitNewStatus(conn); }
        sni.TooltipTitle = st is null ? "kairec: daemon not running" : recording ? $"kairec: recording {st.Profile}" : paused ? "kairec: auto recording off" : "kairec: idle";
        sni.TooltipBody = st is null ? "" : recording ? $"{st.Mode} · {st.MarkerCount} markers" : paused ? $"paused {st.Paused}" : "waiting for a profiled game";
        sni.EmitNewIcon(conn); sni.EmitNewToolTip(conn);

        var items = new List<MenuItem>();
        items.Add(new MenuItem { Label = st is null ? "daemon not running" : recording ? $"● recording {st.Profile} ({st.Mode}) · {st.MarkerCount} markers" : paused ? $"auto recording off {st.Paused}" : "idle", Enabled = false });
        items.Add(MenuItem.Sep());
        if (st is not null)
        {
            if (paused) items.Add(new MenuItem { Label = "Turn recording on", Action = () => Cmd("resume") });
            else items.Add(new MenuItem
            {
                Label = "Turn recording off",
                Children =
                {
                    new MenuItem { Label = "for the rest of this game", Action = () => Cmd("pause", "session") },
                    new MenuItem { Label = "for 1 hour", Action = () => Cmd("pause", "until", 1) },
                    new MenuItem { Label = "for 2 hours", Action = () => Cmd("pause", "until", 2) },
                    new MenuItem { Label = "for 8 hours", Action = () => Cmd("pause", "until", 8) },
                    new MenuItem { Label = "until I turn it back on", Action = () => Cmd("pause", "forever") },
                },
            });
            items.Add(new MenuItem { Label = "Mark", Enabled = recording, Action = () => Cmd("mark") });
            items.Add(new MenuItem { Label = $"Save last {st.SaveSeconds ?? 30} s", Enabled = recording, Action = () => Cmd("save") });
            items.Add(new MenuItem { Label = "Stop session", Enabled = recording, Action = () => Cmd("stop") });
            items.Add(MenuItem.Sep());
        }
        items.Add(new MenuItem { Label = "Open web UI", Enabled = uiUrl is not null, Action = OpenUi });
        items.Add(new MenuItem { Label = "Open clips folder", Enabled = rootDir is not null, Action = () => { if (rootDir is not null) Open(Path.Combine(rootDir, "clips")); } });
        items.Add(MenuItem.Sep());
        items.Add(new MenuItem { Label = "Quit tray", Action = () => Environment.Exit(0) });
        menu.SetItems(items);
        menu.EmitLayoutUpdated(conn);
    }
    await Task.Delay(3000);
}
