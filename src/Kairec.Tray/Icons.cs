namespace Kairec.Tray;

/// <summary>Tiny generated icons: a filled circle with a ring, in the colour of the state. ARGB32, network byte order, as SNI wants.</summary>
static class Icons
{
    public static (int W, int H, byte[] Argb) Circle(int size, (byte R, byte G, byte B) fill, (byte R, byte G, byte B) ring, bool hollow = false)
    {
        var px = new byte[size * size * 4];
        var c = (size - 1) / 2.0; var r = size / 2.0 - 1.0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            byte a = 0; (byte R, byte G, byte B) col = fill;
            if (d <= r - 1.5) { a = hollow ? (byte)0 : (byte)255; }
            else if (d <= r + 0.5) { a = (byte)Math.Clamp((r + 0.5 - d) * 255, 0, 255); col = ring; }
            if (hollow && d <= r - 1.5 && d >= r - 3.5) { a = 255; col = ring; }
            var i = (y * size + x) * 4;
            px[i] = a; px[i + 1] = col.R; px[i + 2] = col.G; px[i + 3] = col.B;
        }
        return (size, size, px);
    }

    public static readonly (int, int, byte[]) Idle = Circle(22, (0x5b, 0x66, 0x7a), (0xa0, 0xa8, 0xb4), hollow: true);
    public static readonly (int, int, byte[]) Recording = Circle(22, (0xe5, 0x39, 0x35), (0xff, 0x8a, 0x80));
    public static readonly (int, int, byte[]) Paused = Circle(22, (0xf5, 0xb5, 0x44), (0xff, 0xd9, 0x8a));
    public static readonly (int, int, byte[]) Gone = Circle(22, (0x3a, 0x3f, 0x4a), (0x55, 0x5c, 0x68), hollow: true);
}
