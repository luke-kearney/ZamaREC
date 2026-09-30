namespace Kairec.Tray;

/// <summary>
/// The brand mark (packaging/brand/kairec-tray-mono.svg) rasterised in software: a bar, two strokes and a dot on a
/// 120-unit canvas. The dot carries the state colour. ARGB32, network byte order, as SNI wants.
/// </summary>
static class Icons
{
    static readonly (byte R, byte G, byte B) Ink = (0xed, 0xe9, 0xe3);
    static readonly (byte R, byte G, byte B) Dim = (0x6b, 0x72, 0x7d);
    static readonly (byte R, byte G, byte B) Red = (0xff, 0x4a, 0x2b);
    static readonly (byte R, byte G, byte B) Gold = (0xf5, 0xb5, 0x44);

    public static (int W, int H, byte[] Argb) Mark(int size, (byte R, byte G, byte B) ink, (byte R, byte G, byte B) dot, byte inkAlpha = 255)
    {
        const int ss = 4;                       // supersampling per axis
        var px = new byte[size * size * 4];
        var scale = 120.0 / size;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int inkHits = 0, dotHits = 0;
            for (int sy = 0; sy < ss; sy++)
            for (int sx = 0; sx < ss; sx++)
            {
                var u = (x + (sx + 0.5) / ss) * scale;
                var v = (y + (sy + 0.5) / ss) * scale;
                if (InDot(u, v)) dotHits++;
                else if (InInk(u, v)) inkHits++;
            }
            var total = (double)(ss * ss);
            var aDot = dotHits / total;
            var aInk = inkHits / total * (inkAlpha / 255.0);
            var a = aDot + aInk;
            var i = (y * size + x) * 4;
            if (a <= 0) continue;
            px[i] = (byte)Math.Round(a * 255);
            px[i + 1] = (byte)Math.Round((dot.R * aDot + ink.R * aInk) / a);
            px[i + 2] = (byte)Math.Round((dot.G * aDot + ink.G * aInk) / a);
            px[i + 3] = (byte)Math.Round((dot.B * aDot + ink.B * aInk) / a);
        }
        return (size, size, px);
    }

    static bool InDot(double u, double v) => (u - 52) * (u - 52) + (v - 60) * (v - 60) <= 11 * 11;

    static bool InInk(double u, double v) =>
        (u >= 20 && u <= 34 && v >= 16 && v <= 104)                 // the bar
        || NearSegment(u, v, 67.9, 46.3, 96, 22, 7)                  // upper stroke
        || NearSegment(u, v, 67.9, 73.7, 96, 98, 7);                 // lower stroke

    // Distance to a segment with butt caps, like an SVG line with the default linecap.
    static bool NearSegment(double u, double v, double x1, double y1, double x2, double y2, double halfWidth)
    {
        var dx = x2 - x1; var dy = y2 - y1; var len2 = dx * dx + dy * dy;
        var t = ((u - x1) * dx + (v - y1) * dy) / len2;
        if (t < 0 || t > 1) return false;
        var px = x1 + t * dx; var py = y1 + t * dy;
        return (u - px) * (u - px) + (v - py) * (v - py) <= halfWidth * halfWidth;
    }

    public static readonly (int, int, byte[]) Idle = Mark(22, Ink, Dim);
    public static readonly (int, int, byte[]) Recording = Mark(22, Ink, Red);
    public static readonly (int, int, byte[]) Paused = Mark(22, Ink, Gold);
    public static readonly (int, int, byte[]) Gone = Mark(22, Dim, Dim, inkAlpha: 160);

    // No dashes in the names: Plasma's lookup strips "-suffixes" theme by theme, so "kairec-tray-idle" resolves
    // to the installed app icon "kairec" before IconThemePath is even searched.
    public const string IdleName = "kairec_tray_idle", RecordingName = "kairec_tray_recording", PausedName = "kairec_tray_paused", GoneName = "kairec_tray_gone";

    // The same mark as an SVG in the Breeze convention: the host replaces the "current-color-scheme" stylesheet
    // with its panel colours, so the ink follows light and dark panels. The dot keeps its state colour.
    // The strokes are polygons because only fill:currentColor is reliably recoloured.
    static string Svg((byte R, byte G, byte B) dot, string inkOpacity = "1") => $$"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120" width="22" height="22">
          <style type="text/css" id="current-color-scheme">.ColorScheme-Text { color:#232629; }</style>
          <g class="ColorScheme-Text" style="fill:currentColor;fill-opacity:{{inkOpacity}};stroke:none">
            <rect x="20" y="16" width="14" height="88" rx="2"/>
            <path d="M72.48 51.59 L100.58 27.29 L91.42 16.71 L63.32 41.01 Z"/>
            <path d="M72.48 68.41 L100.58 92.71 L91.42 103.29 L63.32 78.99 Z"/>
          </g>
          <circle cx="52" cy="60" r="11" style="fill:#{{dot.R:x2}}{{dot.G:x2}}{{dot.B:x2}}"/>
        </svg>
        """;

    /// <summary>Writes the themed icons to a runtime dir and returns it, for the item's IconThemePath. Null if that fails; the pixmaps still work.</summary>
    public static string? WriteThemeDir()
    {
        try
        {
            var theme = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), "kairec-tray-icons");
            var dir = Path.Combine(theme, "hicolor", "scalable", "status");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, IdleName + ".svg"), Svg(Dim));
            File.WriteAllText(Path.Combine(dir, RecordingName + ".svg"), Svg(Red));
            File.WriteAllText(Path.Combine(dir, PausedName + ".svg"), Svg(Gold));
            File.WriteAllText(Path.Combine(dir, GoneName + ".svg"), Svg(Dim, "0.6"));
            return theme;
        }
        catch (Exception ex) { Console.Error.WriteLine("could not write tray icons: " + ex.Message); return null; }
    }
}
