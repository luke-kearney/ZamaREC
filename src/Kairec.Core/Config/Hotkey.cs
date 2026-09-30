namespace Kairec.Core.Config;

/// <summary>A parsed "ctrl+alt+m" combination: Win32 modifier flags and a virtual-key code. Parsed on both systems, registered only on Windows.</summary>
public sealed record Hotkey(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint Alt = 1, Control = 2, Shift = 4, Win = 8, NoRepeat = 0x4000;

    public static Hotkey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0; uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": mods |= Control; continue;
                case "alt": mods |= Alt; continue;
                case "shift": mods |= Shift; continue;
                case "win" or "super" or "meta": mods |= Win; continue;
            }
            if (vk != 0) return null;
            vk = KeyCode(part);
            if (vk == 0) return null;
        }
        return vk == 0 ? null : new Hotkey(mods, vk, text.Trim());
    }

    private static uint KeyCode(string k)
    {
        if (k.Length == 1)
        {
            var c = char.ToUpperInvariant(k[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return c switch { '`' => 0xC0, '-' => 0xBD, '=' => 0xBB, '[' => 0xDB, ']' => 0xDD, '\\' => 0xDC, ';' => 0xBA, '\'' => 0xDE, ',' => 0xBC, '.' => 0xBE, '/' => 0xBF, _ => 0 };
        }
        if (k.Length is 2 or 3 && k[0] == 'f' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        if (k.StartsWith("numpad") && int.TryParse(k[6..], out var n) && n is >= 0 and <= 9) return (uint)(0x60 + n);
        return k switch
        {
            "space" => 0x20, "enter" or "return" => 0x0D, "tab" => 0x09, "escape" or "esc" => 0x1B, "backspace" => 0x08,
            "insert" or "ins" => 0x2D, "delete" or "del" => 0x2E, "home" => 0x24, "end" => 0x23, "pageup" or "pgup" => 0x21, "pagedown" or "pgdn" => 0x22,
            "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28, "pause" => 0x13, "printscreen" or "print" => 0x2C, "scrolllock" => 0x91,
            "numlock" => 0x90, "capslock" => 0x14, "multiply" => 0x6A, "add" => 0x6B, "subtract" => 0x6D, "decimal" => 0x6E, "divide" => 0x6F,
            _ => 0,
        };
    }
}
