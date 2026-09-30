using Tmds.DBus.Protocol;

namespace Kairec.Tray;

/// <summary>A menu item. Ids are assigned when the layout is (re)built. Children make it a submenu.</summary>
sealed class MenuItem
{
    public string Label = "";
    public bool Enabled = true;
    public bool Separator;
    public Action? Action;
    public List<MenuItem> Children = new();
    public int Id;
    public static MenuItem Sep() => new() { Separator = true };
}

/// <summary>com.canonical.dbusmenu at /MenuBar. Plasma calls GetLayout, we describe the tree; clicks arrive as Event("clicked").</summary>
sealed class DbusMenu : IPathMethodHandler
{
    public const string Iface = "com.canonical.dbusmenu";
    public string Path => "/MenuBar";
    public bool HandlesChildPaths => false;

    private readonly object _lock = new();
    private MenuItem _root = new();
    private Dictionary<int, MenuItem> _byId = new();
    private uint _revision = 1;
    private int _nextId = 1;

    public void SetItems(List<MenuItem> items)
    {
        lock (_lock)
        {
            _root = new MenuItem { Children = items };
            // Ids are never reused. Plasma keeps what it knew about an id, so a plain item that took the id of
            // the submenu before it ("Turn recording on" after "Turn recording off") showed up greyed out.
            _byId = new();
            void Assign(MenuItem m) { foreach (var c in m.Children) { c.Id = _nextId++; _byId[c.Id] = c; Assign(c); } }
            _root.Id = 0; _byId[0] = _root; Assign(_root);
            _revision++;
        }
    }

    public void EmitLayoutUpdated(DBusConnection c)
    {
        var w = c.GetMessageWriter();
        w.WriteSignalHeader(null, Path, Iface, "LayoutUpdated", "ui"); w.WriteUInt32(_revision); w.WriteInt32(0);
        c.TrySendMessage(w.CreateMessage()); w.Dispose();
    }

    public ValueTask HandleMethodAsync(MethodContext ctx)
    {
        var m = ctx.Request;
        switch (m.InterfaceAsString, m.MemberAsString)
        {
            case ("org.freedesktop.DBus.Properties", "GetAll"):
            {
                var w = ctx.CreateReplyWriter("a{sv}");
                var d = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart(); w.WriteString("Version"); w.VUInt(3);
                w.WriteDictionaryEntryStart(); w.WriteString("Status"); w.VStr("normal");
                w.WriteDictionaryEntryStart(); w.WriteString("TextDirection"); w.VStr("ltr");
                w.WriteDictionaryEntryStart(); w.WriteString("IconThemePath"); w.WriteSignature("as"); var a = w.WriteArrayStart(DBusType.String); w.WriteArrayEnd(a);
                w.WriteDictionaryEnd(d);
                ctx.Reply(w.CreateMessage()); w.Dispose(); return default;
            }
            case ("org.freedesktop.DBus.Properties", "Get"):
            {
                var r = m.GetBodyReader(); r.ReadString(); var name = r.ReadString();
                var w = ctx.CreateReplyWriter("v");
                switch (name)
                {
                    case "Version": w.VUInt(3); break;
                    case "Status": w.VStr("normal"); break;
                    case "TextDirection": w.VStr("ltr"); break;
                    case "IconThemePath": { w.WriteSignature("as"); var a = w.WriteArrayStart(DBusType.String); w.WriteArrayEnd(a); break; }
                    default: ctx.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name); return default;
                }
                ctx.Reply(w.CreateMessage()); w.Dispose(); return default;
            }
            case ("org.freedesktop.DBus.Introspectable", "Introspect"):
                ctx.ReplyIntrospectXml([System.Text.Encoding.UTF8.GetBytes(IntrospectXml)], []); return default;
            case (Iface, "GetLayout"):
            {
                var r = m.GetBodyReader(); var parent = r.ReadInt32(); var depth = r.ReadInt32();
                var w = ctx.CreateReplyWriter("u(ia{sv}av)");
                lock (_lock)
                {
                    w.WriteUInt32(_revision);
                    WriteNode(ref w, _byId.TryGetValue(parent, out var p) ? p : _root, depth);
                }
                ctx.Reply(w.CreateMessage()); w.Dispose(); return default;
            }
            case (Iface, "GetGroupProperties"):
            {
                var r = m.GetBodyReader();
                var ids = new List<int>(); var arr = r.ReadArrayStart(DBusType.Int32); while (r.HasNext(arr)) ids.Add(r.ReadInt32());
                var w = ctx.CreateReplyWriter("a(ia{sv})");
                var a = w.WriteArrayStart(DBusType.Struct);
                lock (_lock)
                    foreach (var id in ids) if (_byId.TryGetValue(id, out var item)) { w.WriteStructureStart(); w.WriteInt32(id); WriteProps(ref w, item); }
                w.WriteArrayEnd(a);
                ctx.Reply(w.CreateMessage()); w.Dispose(); return default;
            }
            case (Iface, "GetProperty"):
            {
                var r = m.GetBodyReader(); var id = r.ReadInt32(); var name = r.ReadString();
                var w = ctx.CreateReplyWriter("v");
                MenuItem? item; lock (_lock) _byId.TryGetValue(id, out item);
                if (item is null) { ctx.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "no such item"); return default; }
                switch (name)
                {
                    case "label": w.VStr(item.Label); break;
                    case "enabled": w.VBool(item.Enabled); break;
                    case "type": w.VStr(item.Separator ? "separator" : "standard"); break;
                    case "children-display": w.VStr(item.Children.Count > 0 ? "submenu" : ""); break;
                    default: w.VStr(""); break;
                }
                ctx.Reply(w.CreateMessage()); w.Dispose(); return default;
            }
            case (Iface, "Event"):
            {
                var r = m.GetBodyReader(); var id = r.ReadInt32(); var ev = r.ReadString();
                if (ev == "clicked") { MenuItem? item; lock (_lock) _byId.TryGetValue(id, out item); item?.Action?.Invoke(); }
                var w = ctx.CreateReplyWriter(""); ctx.Reply(w.CreateMessage()); w.Dispose(); return default;
            }
            case (Iface, "EventGroup"):
            {
                var r = m.GetBodyReader(); var arr = r.ReadArrayStart(DBusType.Struct);
                while (r.HasNext(arr)) { var id = r.ReadInt32(); var ev = r.ReadString(); r.ReadVariantValue(); r.ReadUInt32(); if (ev == "clicked") { MenuItem? item; lock (_lock) _byId.TryGetValue(id, out item); item?.Action?.Invoke(); } }
                var w = ctx.CreateReplyWriter("ai"); var a = w.WriteArrayStart(DBusType.Int32); w.WriteArrayEnd(a); ctx.Reply(w.CreateMessage()); w.Dispose(); return default;
            }
            case (Iface, "AboutToShow"):
            { var w = ctx.CreateReplyWriter("b"); w.WriteBool(false); ctx.Reply(w.CreateMessage()); w.Dispose(); return default; }
            case (Iface, "AboutToShowGroup"):
            { var w = ctx.CreateReplyWriter("aiai"); var a = w.WriteArrayStart(DBusType.Int32); w.WriteArrayEnd(a); var b = w.WriteArrayStart(DBusType.Int32); w.WriteArrayEnd(b); ctx.Reply(w.CreateMessage()); w.Dispose(); return default; }
            default: ctx.ReplyUnknownMethodError(); return default;
        }
    }

    private static void WriteNode(ref MessageWriter w, MenuItem item, int depth)
    {
        w.WriteStructureStart();
        w.WriteInt32(item.Id);
        WriteProps(ref w, item);
        var a = w.WriteArrayStart(DBusType.Variant);
        if (depth != 0)
            foreach (var c in item.Children) { w.WriteSignature("(ia{sv}av)"); WriteNode(ref w, c, depth < 0 ? depth : depth - 1); }
        w.WriteArrayEnd(a);
    }

    private static void WriteProps(ref MessageWriter w, MenuItem item)
    {
        var d = w.WriteDictionaryStart();
        if (item.Separator) { w.WriteDictionaryEntryStart(); w.WriteString("type"); w.VStr("separator"); }
        else
        {
            w.WriteDictionaryEntryStart(); w.WriteString("label"); w.VStr(item.Label);
            // Every property is stated, never left to a default the host may have overridden for that id.
            w.WriteDictionaryEntryStart(); w.WriteString("enabled"); w.VBool(item.Enabled);
            w.WriteDictionaryEntryStart(); w.WriteString("children-display"); w.VStr(item.Children.Count > 0 ? "submenu" : "");
        }
        w.WriteDictionaryEnd(d);
    }

    private const string IntrospectXml = """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/><property name="Status" type="s" access="read"/><property name="TextDirection" type="s" access="read"/><property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout"><arg type="i" name="parentId" direction="in"/><arg type="i" name="recursionDepth" direction="in"/><arg type="as" name="propertyNames" direction="in"/><arg type="u" name="revision" direction="out"/><arg type="(ia{sv}av)" name="layout" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" name="ids" direction="in"/><arg type="as" name="propertyNames" direction="in"/><arg type="a(ia{sv})" name="properties" direction="out"/></method>
          <method name="GetProperty"><arg type="i" name="id" direction="in"/><arg type="s" name="name" direction="in"/><arg type="v" name="value" direction="out"/></method>
          <method name="Event"><arg type="i" name="id" direction="in"/><arg type="s" name="eventId" direction="in"/><arg type="v" name="data" direction="in"/><arg type="u" name="timestamp" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" name="events" direction="in"/><arg type="ai" name="idErrors" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" name="id" direction="in"/><arg type="b" name="needUpdate" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" name="ids" direction="in"/><arg type="ai" name="updatesNeeded" direction="out"/><arg type="ai" name="idErrors" direction="out"/></method>
          <signal name="ItemsPropertiesUpdated"><arg type="a(ia{sv})" name="updatedProps"/><arg type="a(ias)" name="removedProps"/></signal>
          <signal name="LayoutUpdated"><arg type="u" name="revision"/><arg type="i" name="parent"/></signal>
          <signal name="ItemActivationRequested"><arg type="i" name="id"/><arg type="u" name="timestamp"/></signal>
        </interface>
        """;
}
