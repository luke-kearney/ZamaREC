using Tmds.DBus.Protocol;

namespace ZamaRec.Tray;

/// <summary>Variants written as signature + value. MessageWriter is a struct: every helper must take it by ref or the bytes land in a copy.</summary>
public delegate void PropWriter(ref MessageWriter w);
static class VariantWriter
{
    public static void VStr(this ref MessageWriter w, string v) { w.WriteSignature("s"); w.WriteString(v); }
    public static void VBool(this ref MessageWriter w, bool v) { w.WriteSignature("b"); w.WriteBool(v); }
    public static void VInt(this ref MessageWriter w, int v) { w.WriteSignature("i"); w.WriteInt32(v); }
    public static void VUInt(this ref MessageWriter w, uint v) { w.WriteSignature("u"); w.WriteUInt32(v); }
    public static void VPath(this ref MessageWriter w, string v) { w.WriteSignature("o"); w.WriteObjectPath(v); }
}
