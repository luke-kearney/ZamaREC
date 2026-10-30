using ZamaRec.Core.Processes;

namespace ZamaRec.Tests;

public class SteamScreenshotTests
{
    // Two accounts as loginusers.vdf lists them; the account id is the SteamID64 minus 76561197960265728.
    private const string LoginUsers = """
        "users"
        {
        	"76561198000000101"
        	{
        		"AccountName"		"first"
        		"MostRecent"		"0"
        		"Timestamp"		"1790000000"
        	}
        	"76561198000000202"
        	{
        		"AccountName"		"second"
        		"MostRecent"		"1"
        		"Timestamp"		"1780000000"
        	}
        }
        """;

    [Fact]
    public void TakesTheMostRecentAccount()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, LoginUsers);
        Assert.Equal(39734474, SteamLibrary.LastAccount(f));
        // without a MostRecent flag the newest timestamp decides (Steam writes the key in lower case at times)
        File.WriteAllText(f, LoginUsers.Replace("\"MostRecent\"\t\t\"1\"", "\"mostrecent\"\t\t\"0\""));
        Assert.Equal(39734373, SteamLibrary.LastAccount(f));
        File.WriteAllText(f, "\"users\"\n{\n}\n");
        Assert.Null(SteamLibrary.LastAccount(f));
        Assert.Null(SteamLibrary.LastAccount(f + ".missing"));
        File.Delete(f);
    }

    [Fact]
    public void ScreenshotFolderIsUnderTheAccountsUserdata()
    {
        if (OperatingSystem.IsWindows()) return;   // Steam's root comes from the registry there
        var home = Directory.CreateTempSubdirectory("zamarec-test").FullName;
        try
        {
            var steam = Path.Combine(home, ".local/share/Steam");
            Directory.CreateDirectory(Path.Combine(steam, "config"));
            File.WriteAllText(Path.Combine(steam, "config", "loginusers.vdf"), LoginUsers);
            Assert.Null(SteamLibrary.ScreenshotDir(4244510, home));   // no userdata for the account: Steam never ran as it here

            Directory.CreateDirectory(Path.Combine(steam, "userdata", "39734474"));
            Assert.Equal(Path.Combine(steam, "userdata/39734474/760/remote/4244510/screenshots"), SteamLibrary.ScreenshotDir(4244510, home));
        }
        finally { Directory.Delete(home, true); }
    }
}
