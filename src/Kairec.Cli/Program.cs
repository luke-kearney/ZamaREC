using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kairec.Protocol;

// kairec: tiny NativeAOT client for the daemon's Unix socket. Meant to be bound to a KDE shortcut,
// so it must start fast and never open a window.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage();

var req = new ControlRequest { Cmd = args[0] };
var rest = args.Skip(1).ToList();

switch (req.Cmd)
{
    case "mark":
        req.Label = rest.Count > 0 ? string.Join(' ', rest) : null;
        break;
    case "save":
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] is "--last" or "-l" && i + 1 < rest.Count)
            {
                req.Seconds = ParseSeconds(rest[++i]);
                if (req.Seconds is null) { Console.Error.WriteLine($"bad --last value '{rest[i]}' (try 30, 30s, 2m)"); return 2; }
            }
            else req.Label = (req.Label is null ? "" : req.Label + " ") + rest[i];
        }
        break;
    case "start":
        req.Profile = rest.FirstOrDefault();
        break;
    case "top":
        return await Top.RunAsync();
    case "status":
        if (rest.Contains("--watch") || rest.Contains("-w")) return await Top.RunAsync();
        break;
    case "stop" or "reload" or "config":
        break;
    default:
        Console.Error.WriteLine($"unknown command '{req.Cmd}'");
        return Usage(2);
}

try
{
    var resp = Daemon.Send(req);
    if (resp is null) { Console.Error.WriteLine("empty reply from daemon"); return 1; }
    Console.WriteLine(resp.Message);
    return resp.Ok ? 0 : 1;
}
catch (SocketException)
{
    Console.Error.WriteLine($"daemon not running (no socket at {ControlPaths.SocketPath()}). Start it with: systemctl --user start kairec");
    return 3;
}


static int? ParseSeconds(string s)
{
    s = s.Trim().ToLowerInvariant();
    var mult = 1;
    if (s.EndsWith('s')) s = s[..^1];
    else if (s.EndsWith('m')) { s = s[..^1]; mult = 60; }
    return int.TryParse(s, out var v) && v > 0 ? v * mult : null;
}

static int Usage(int code = 0)
{
    Console.WriteLine("""
        kairec - session recorder control

          kairec mark [label...]            drop a marker on the current session
          kairec save [--last 30s] [label]  save the last N seconds from the replay buffer
          kairec start [profile]            start a session manually
          kairec stop                       stop the current session
          kairec status                     show what the daemon is doing
        """);
    return code;
}
