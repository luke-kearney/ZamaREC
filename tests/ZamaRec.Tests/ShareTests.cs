using ZamaRec.Core.Config;
using ZamaRec.Core.Export;

namespace ZamaRec.Tests;

public class ShareTests
{
    [Fact]
    public void ThePictureStepsDownAsTheBudgetShrinks()
    {
        // 40 s of 1440p60 into 50, 25 and 10 MB
        Assert.Equal((1080, 60), Pick(50_000_000, 40));
        Assert.Equal((720, 60), Pick(25_000_000, 40));
        Assert.Equal((720, 30), Pick(10_000_000, 40));
        // a long clip into a small file ends at the bottom of the ladder
        Assert.Equal((360, 30), Pick(10_000_000, 200));
        // and past that there is nothing worth making
        Assert.Null(ClipExporter.PlanShare(5_000_000, 200, 2560, 1440, 60));
        // a short clip that fits as it is keeps its size and rate
        Assert.Equal((1440, 60), Pick(100_000_000, 20));
        // never bigger or faster than the source
        var small = ClipExporter.PlanShare(100_000_000, 10, 1280, 720, 30)!;
        Assert.Equal((720, 30), (small.Height, small.Fps));
    }

    [Fact]
    public void TheBitratesAddUpToLessThanTheCap()
    {
        foreach (var (bytes, seconds) in new[] { (10_000_000L, 30.0), (25_000_000L, 90.0), (50_000_000L, 12.5) })
        {
            var p = ClipExporter.PlanShare(bytes, seconds, 1920, 1080, 60)!;
            var planned = (p.VideoKbps + p.AudioKbps) * 1000.0 / 8 * seconds;
            Assert.InRange(planned, bytes * 0.85, bytes * 0.95);
        }
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", true)]
    [InlineData("https://canary.discord.com/api/webhooks/123/abc", true)]
    [InlineData("http://127.0.0.1:4558/api/webhooks/1/test", true)]            // loopback, for tests
    [InlineData("http://discord.com/api/webhooks/123/abc", false)]              // not https
    [InlineData("https://discord.com/channels/1/2", false)]                     // not a webhook
    [InlineData("https://example.com/api/webhooks/123/abc", false)]
    [InlineData("https://discord.com.example.org/api/webhooks/123/abc", false)]
    [InlineData("not a url", false)]
    public void OnlyDiscordWebhookAddressesAreAccepted(string url, bool ok) => Assert.Equal(ok, ConfigLoader.LooksLikeDiscordWebhook(url));

    [Fact]
    public void TheShareSectionRoundTrips()
    {
        var cfg = ConfigLoader.Parse("[share]\ndiscord_webhook = \"https://discord.com/api/webhooks/1/x\"\ndiscord_max_mb = 25\n");
        var back = ConfigLoader.Parse(ConfigLoader.Serialize(cfg));
        Assert.Equal("https://discord.com/api/webhooks/1/x", back.Share.DiscordWebhook);
        Assert.Equal(25, back.Share.DiscordMaxMb);
        Assert.Null(ConfigLoader.Parse("").Share.DiscordWebhook);
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[share]\ndiscord_webhook = \"https://example.com/hook\"\n"));
    }

    private static (int, int)? Pick(long bytes, double seconds) =>
        ClipExporter.PlanShare(bytes, seconds, 2560, 1440, 60) is { } p ? (p.Height, p.Fps) : null;
}
