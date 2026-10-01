using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZamaRec.Protocol;

namespace ZamaRec.Tray;

/// <summary>Talks to the daemon over its Unix socket, same as the CLI.</summary>
static class DaemonClient
{
    public static ControlResponse? Send(ControlRequest req)
    {
        try
        {
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            sock.ReceiveTimeout = 15000; sock.SendTimeout = 5000;
            sock.Connect(new UnixDomainSocketEndPoint(ControlPaths.SocketPath()));
            sock.Send(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, ProtocolJson.Default.ControlRequest) + "\n"));
            var buf = new byte[8192]; var sb = new StringBuilder();
            while (true)
            {
                var n = sock.Receive(buf);
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().Contains('\n')) break;
            }
            return JsonSerializer.Deserialize(sb.ToString().Split('\n')[0], ProtocolJson.Default.ControlResponse);
        }
        catch { return null; }
    }

    public static DaemonStatus? Status() => Send(new ControlRequest { Cmd = "status" })?.Status;
}
