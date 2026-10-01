using ZamaRec.Core.Config;
using Xunit;

public class HotkeyTests
{
    [Theory]
    [InlineData("ctrl+alt+m", Hotkey.Control | Hotkey.Alt, 'M')]
    [InlineData("Ctrl + Shift + F5", Hotkey.Control | Hotkey.Shift, 0x74u)]
    [InlineData("win+numpad0", Hotkey.Win, 0x60u)]
    [InlineData("pause", 0u, 0x13u)]
    [InlineData("ctrl+alt+7", Hotkey.Control | Hotkey.Alt, '7')]
    public void ParsesCombinations(string text, uint mods, uint vk)
    {
        var hk = Hotkey.TryParse(text);
        Assert.NotNull(hk);
        Assert.Equal(mods, hk!.Modifiers);
        Assert.Equal(vk, hk.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl+alt")]          // no key
    [InlineData("ctrl+m+n")]          // two keys
    [InlineData("hyper+m")]           // unknown modifier
    [InlineData("ctrl+f99")]
    public void RejectsNonsense(string text) => Assert.Null(Hotkey.TryParse(text));

    [Fact]
    public void DefaultConfigHotkeysParse()
    {
        var cfg = ConfigLoader.Parse(DefaultConfig.ForThisSystem());
        Assert.NotNull(Hotkey.TryParse(cfg.Hotkeys.Mark));
        Assert.NotNull(Hotkey.TryParse(cfg.Hotkeys.Save));
        Assert.NotNull(Hotkey.TryParse(cfg.Hotkeys.Ui));
        Assert.Equal("auto", cfg.Capture.Gpu);
    }

    [Fact]
    public void BadHotkeyIsAConfigError()
    {
        var toml = DefaultConfig.Toml.Replace("mark = \"ctrl+alt+m\"", "mark = \"ctrl+alt\"");
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse(toml));
        Assert.Contains("hotkeys.mark", ex.Message);
    }
}
