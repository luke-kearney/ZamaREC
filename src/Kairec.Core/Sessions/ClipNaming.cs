using System.Text.Json;

namespace Kairec.Core.Sessions;

public static class ClipNaming
{
    /// <summary>Moves a file the recorder just wrote into clips/&lt;profile&gt;/ with a readable name. Returns the final path.</summary>
    public static string Place(string clipsDir, string sourcePath, string profile, string? label)
    {
        var target = NewPath(clipsDir, profile, label, Path.GetExtension(sourcePath));
        File.Move(sourcePath, target);
        return target;
    }

    /// <summary>A fresh clips/&lt;profile&gt;/&lt;stamp&gt;[_label].mp4 path that does not exist yet.</summary>
    public static string NewPath(string clipsDir, string profile, string? label, string ext = ".mp4")
    {
        var dir = Path.Combine(clipsDir, profile);
        Directory.CreateDirectory(dir);
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var name = stamp + (string.IsNullOrWhiteSpace(label) ? "" : "_" + Sanitize(label)) + ext;
        var target = Path.Combine(dir, name);
        for (int i = 2; File.Exists(target); i++)
            target = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + $"_{i}" + ext);
        return target;
    }

    public static void WriteSidecar(string clipPath, ClipInfo info) =>
        File.WriteAllText(clipPath + ".json", JsonSerializer.Serialize(info, SessionJson.Default.ClipInfo));

    public static string Sanitize(string s)
    {
        var chars = s.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var r = new string(chars);
        while (r.Contains("--")) r = r.Replace("--", "-");
        return r.Trim('-').Length == 0 ? "clip" : r.Trim('-')[..Math.Min(40, r.Trim('-').Length)];
    }
}
