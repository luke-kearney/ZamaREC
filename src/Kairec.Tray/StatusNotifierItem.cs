using Tmds.DBus.Protocol;

namespace Kairec.Tray;

/// <summary>org.kde.StatusNotifierItem at /StatusNotifierItem: the icon, tooltip and click handling. Plasma reads properties, we emit New* signals.</summary>
sealed class StatusNotifierItem : IPathMethodHandler
{
    public const string Iface = "org.kde.StatusNotifierItem";
    public string Path => "/StatusNotifierItem";
    public bool HandlesChildPaths => false;

    public string Status = "Active";
    public string Title = "kairec";
    public string TooltipTitle = "kairec";
    public string TooltipBody = "";
    public (int W, int H, byte[] Argb) Icon = Icons.Idle;

    public Action? OnActivate;          // left click
    public Action? OnSecondaryActivate; // middle click

    public ValueTask HandleMethodAsync(MethodContext ctx)
    {
        var m = ctx.Request;
        switch (m.InterfaceAsString, m.MemberAsString)
        {
            case ("org.freedesktop.DBus.Properties", "GetAll"):
            {
                var w = ctx.CreateReplyWriter("a{sv}");
                var dict = w.WriteDictionaryStart();
                foreach (var (k, write) in Properties()) { w.WriteDictionaryEntryStart(); w.WriteString(k); write(ref w); }
                w.WriteDictionaryEnd(dict);
                ctx.Reply(w.CreateMessage()); w.Dispose();
                return default;
            }
            case ("org.freedesktop.DBus.Properties", "Get"):
            {
                var r = m.GetBodyReader(); r.ReadString(); var name = r.ReadString();
                var prop = Properties().FirstOrDefault(p => p.Key == name);
                if (prop.Key is null) { ctx.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name); return default; }
                var w = ctx.CreateReplyWriter("v"); prop.Write(ref w); ctx.Reply(w.CreateMessage()); w.Dispose();
                return default;
            }
            case ("org.freedesktop.DBus.Introspectable", "Introspect"):
                ctx.ReplyIntrospectXml([System.Text.Encoding.UTF8.GetBytes(IntrospectXml)], []);
                return default;
            case (Iface, "Activate"): OnActivate?.Invoke(); Ack(ctx); return default;
            case (Iface, "SecondaryActivate"): OnSecondaryActivate?.Invoke(); Ack(ctx); return default;
            case (Iface, "ContextMenu") or (Iface, "Scroll") or (Iface, "ProvideXdgActivationToken"): Ack(ctx); return default;
            default: ctx.ReplyUnknownMethodError(); return default;
        }
    }

    private static void Ack(MethodContext ctx) { var w = ctx.CreateReplyWriter(""); ctx.Reply(w.CreateMessage()); w.Dispose(); }

    private IEnumerable<(string Key, PropWriter Write)> Properties()
    {
        yield return ("Category", (ref MessageWriter w) => w.VStr("ApplicationStatus"));
        yield return ("Id", (ref MessageWriter w) => w.VStr("kairec"));
        yield return ("Title", (ref MessageWriter w) => w.VStr(Title));
        yield return ("Status", (ref MessageWriter w) => w.VStr(Status));
        yield return ("WindowId", (ref MessageWriter w) => w.VInt(0));
        yield return ("IconName", (ref MessageWriter w) => w.VStr(""));
        yield return ("IconThemePath", (ref MessageWriter w) => w.VStr(""));
        yield return ("OverlayIconName", (ref MessageWriter w) => w.VStr(""));
        yield return ("AttentionIconName", (ref MessageWriter w) => w.VStr(""));
        yield return ("AttentionMovieName", (ref MessageWriter w) => w.VStr(""));
        yield return ("ItemIsMenu", (ref MessageWriter w) => w.VBool(false));
        yield return ("Menu", (ref MessageWriter w) => w.VPath("/MenuBar"));
        yield return ("IconPixmap", (ref MessageWriter w) => { w.WriteSignature("a(iiay)"); WritePixmaps(ref w, Icon); });
        yield return ("OverlayIconPixmap", (ref MessageWriter w) => { w.WriteSignature("a(iiay)"); WritePixmaps(ref w, null); });
        yield return ("AttentionIconPixmap", (ref MessageWriter w) => { w.WriteSignature("a(iiay)"); WritePixmaps(ref w, null); });
        yield return ("ToolTip", (ref MessageWriter w) =>
        {
            w.WriteSignature("(sa(iiay)ss)");
            w.WriteStructureStart(); w.WriteString(""); WritePixmaps(ref w, Icon); w.WriteString(TooltipTitle); w.WriteString(TooltipBody);
        });
    }

    private static void WritePixmaps(ref MessageWriter w, (int W, int H, byte[] Argb)? icon)
    {
        var a = w.WriteArrayStart(DBusType.Struct);
        if (icon is { } i) { w.WriteStructureStart(); w.WriteInt32(i.W); w.WriteInt32(i.H); w.WriteArray(i.Argb); }
        w.WriteArrayEnd(a);
    }

    public void EmitNewIcon(DBusConnection c) => Emit(c, "NewIcon");
    public void EmitNewToolTip(DBusConnection c) => Emit(c, "NewToolTip");
    public void EmitNewStatus(DBusConnection c)
    {
        var w = c.GetMessageWriter();
        w.WriteSignalHeader(null, Path, Iface, "NewStatus", "s"); w.WriteString(Status);
        c.TrySendMessage(w.CreateMessage()); w.Dispose();
    }
    private void Emit(DBusConnection c, string signal)
    {
        var w = c.GetMessageWriter();
        w.WriteSignalHeader(null, Path, Iface, signal, "");
        c.TrySendMessage(w.CreateMessage()); w.Dispose();
    }

    private const string IntrospectXml = """
        <interface name="org.kde.StatusNotifierItem">
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <property name="Category" type="s" access="read"/><property name="Id" type="s" access="read"/><property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/><property name="WindowId" type="i" access="read"/><property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/><property name="OverlayIconName" type="s" access="read"/><property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/><property name="AttentionIconPixmap" type="a(iiay)" access="read"/><property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/><property name="ItemIsMenu" type="b" access="read"/><property name="Menu" type="o" access="read"/><property name="IconThemePath" type="s" access="read"/>
          <signal name="NewTitle"/><signal name="NewIcon"/><signal name="NewAttentionIcon"/><signal name="NewOverlayIcon"/><signal name="NewToolTip"/><signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface>
        """;
}
