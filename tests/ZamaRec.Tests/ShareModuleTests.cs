using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ZamaRec.Core.Config;
using ZamaRec.Core.Share;

namespace ZamaRec.Tests;

public class ShareModuleTests
{
    private const string Toml = """
        [share]
        discord_max_mb = 25

        [[share.targets]]
        name = "Our clips"
        command = "~/bin/clips-upload"
        args = ["--quiet"]
        max_mb = 200
        [share.targets.settings]
        url = "https://clips.example"
        apiToken = "s3cret"
        """;

    [Fact]
    public void TargetsRoundTripWithTheirSettingsUntouched()
    {
        var back = ConfigLoader.Parse(ConfigLoader.Serialize(ConfigLoader.Parse(Toml)));
        var t = Assert.Single(back.Share.Targets);
        Assert.Equal(("Our clips", "~/bin/clips-upload", 200d), (t.Name, t.Command, t.MaxMb));
        Assert.Equal(["--quiet"], t.Args);
        Assert.Equal("https://clips.example", t.Settings["url"]);
        Assert.Equal("s3cret", t.Settings["apiToken"]);   // a module's own key: not renamed to snake case
        Assert.Same(t, back.Share.FindTarget("our CLIPS"));
        Assert.Empty(ConfigLoader.Parse("").Share.Targets);
    }

    [Theory]
    [InlineData("command = \"/bin/true\"")]                                     // no name
    [InlineData("name = \"x\"")]                                                // no command
    [InlineData("name = \"Discord\"\ncommand = \"/bin/true\"")]                 // the built-in target's name
    [InlineData("name = \"a/b\"\ncommand = \"/bin/true\"")]
    [InlineData("name = \"x\"\ncommand = \"/bin/true\"\nmax_mb = -1")]
    [InlineData("name = \"x\"\ncommand = \"/bin/true\"\n[[share.targets]]\nname = \"X\"\ncommand = \"/bin/true\"")]
    public void ABadTargetIsRefused(string body) =>
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Parse("[[share.targets]]\n" + body + "\n"));

    [Fact]
    public void AModulesOutputIsReadLineByLine()
    {
        Assert.Null(ShareModule.ParseLine("uploading chunk 3"));
        Assert.Null(ShareModule.ParseLine("{ not json"));
        Assert.Null(ShareModule.ParseLine("{\"other\": 1}"));
        Assert.Equal(new ShareModule.Line(0.5, "chunk 3 of 6", null), ShareModule.ParseLine("{\"progress\": 0.5, \"note\": \"chunk 3 of 6\"}"));
        Assert.Equal(1, ShareModule.ParseLine("{\"progress\": 7}")!.Value.Progress);
        Assert.Equal(new ShareResult(true, "https://clips.example/c/1", null), ShareModule.ParseLine("{\"ok\": true, \"url\": \"https://clips.example/c/1\"}")!.Value.Result);
        Assert.Equal(new ShareResult(false, null, "token refused"), ShareModule.ParseLine("{\"ok\": false, \"error\": \"token refused\"}")!.Value.Result);
    }

    private static ShareTargetConfig Script(string body, out string dir)
    {
        dir = Directory.CreateTempSubdirectory("zamarec-share-").FullName;
        var path = Path.Combine(dir, "module");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new ShareTargetConfig { Name = "test", Command = path, Args = ["first"], Settings = { ["token"] = "s3cret" } };
    }

    private static Task<ShareResult> Run(ShareTargetConfig t, List<(double?, string?)>? seen = null, CancellationToken ct = default) =>
        ShareModule.RunAsync(t, new ShareRequest { File = "/clips/a.mp4", FileName = "a.mp4", Bytes = 42, Title = "ace é", Profile = "cs2", SteamAppId = 730 }, (p, n) => seen?.Add((p, n)), NullLogger.Instance, ct);

    [Fact]
    public async Task AModuleGetsTheRequestOnStdinAndReportsBack()
    {
        if (OperatingSystem.IsWindows()) return;
        // the module keeps what it was given, so the test can look at it
        var t = Script("echo \"$1\" > args; cat > request.json\necho 'starting'\necho '{\"progress\": 0.5, \"note\": \"half\"}'\necho '{\"ok\": true, \"url\": \"https://clips.example/c/1\"}'", out var dir);
        try
        {
            var seen = new List<(double?, string?)>();
            var r = await Run(t, seen);
            Assert.Equal(new ShareResult(true, "https://clips.example/c/1", null), r);
            Assert.Equal([(0.5, "half")], seen);
            Assert.Equal("first", File.ReadAllText(Path.Combine(dir, "args")).Trim());
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "request.json")));
            var o = doc.RootElement;
            Assert.Equal(1, o.GetProperty("version").GetInt32());
            Assert.Equal("/clips/a.mp4", o.GetProperty("file").GetString());
            Assert.Equal(42, o.GetProperty("bytes").GetInt64());
            Assert.Equal("ace é", o.GetProperty("title").GetString());
            Assert.Equal(730, o.GetProperty("steam_app_id").GetInt64());
            Assert.False(o.TryGetProperty("duration_seconds", out _));   // what is not known is left out
            Assert.Equal("s3cret", o.GetProperty("settings").GetProperty("token").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task AModuleThatFailsSaysWhy()
    {
        if (OperatingSystem.IsWindows()) return;
        var t = Script("cat > /dev/null\necho 'the server said no' >&2\nexit 3", out var dir);
        try
        {
            var r = await Run(t);
            Assert.False(r.Ok);
            Assert.Contains("code 3", r.Error);
            Assert.Contains("the server said no", r.Error);

            File.WriteAllText(t.Command, "#!/bin/sh\ncat > /dev/null\n");
            Assert.Contains("without saying", (await Run(t)).Error);

            t.Command = Path.Combine(dir, "gone");
            Assert.Contains("not found", (await Run(t)).Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ACancelKillsTheModule()
    {
        if (OperatingSystem.IsWindows()) return;
        var t = Script("cat > /dev/null\necho '{\"progress\": 0.1}'\nexec sleep 60", out var dir);
        try
        {
            using var cts = new CancellationTokenSource();
            var seen = new List<(double?, string?)>();
            var run = Run(t, seen, cts.Token);
            while (seen.Count == 0 && !run.IsCompleted) await Task.Delay(20);
            cts.Cancel();
            var started = DateTime.UtcNow;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(dir, true); }
    }
}
