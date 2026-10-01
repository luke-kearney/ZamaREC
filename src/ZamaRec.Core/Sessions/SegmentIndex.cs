using System.Globalization;
using System.Text.Json;

namespace ZamaRec.Core.Sessions;

/// <summary>
/// Tracks the segments of a timeline session. The recorder's playlist only lists the current window,
/// so the daemon re-reads it as it changes and remembers every segment it has ever seen, with each
/// segment's start offset from the first frame. That is what turns a marker time into a file and offset.
/// </summary>
public sealed class SegmentIndex
{
    private readonly object _lock = new();
    private readonly string _dir;
    private readonly string _media;
    private readonly SegmentFile _file;
    private DateTime _playlistMtime;

    public SegmentIndex(string sessionDir, string? mediaDir = null)
    {
        _dir = sessionDir; _media = mediaDir ?? sessionDir;
        var path = IndexPath;
        _file = File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), SessionJson.Default.SegmentFile) ?? new SegmentFile()
            : new SegmentFile();
    }

    private string IndexPath => Path.Combine(_dir, "segments.json");
    private string PlaylistPath => Path.Combine(_media, Recording.GsrArguments.PlaylistName);
    public string MediaDir => _media;

    public IReadOnlyList<SegmentEntry> All { get { lock (_lock) return _file.Segments.ToList(); } }

    /// <summary>Total bytes of every segment ever seen, including ones since purged.</summary>
    public long TotalBytes { get { lock (_lock) return _file.Segments.Sum(s => s.Bytes); } }

    /// <summary>Total seconds recorded so far, including segments already purged.</summary>
    public double TotalSeconds { get { lock (_lock) { var l = _file.Segments.LastOrDefault(); return l is null ? 0 : l.Start + l.Duration; } } }

    /// <summary>
    /// Segments whose files still exist, i.e. the current window. While recording, the segment being
    /// written is not in the playlist yet; it is appended as an open-ended tail so "the last 30 s" can include it.
    /// </summary>
    public List<SegmentEntry> Available(bool includeTail = true)
    {
        lock (_lock)
        {
            var list = _file.Segments.Where(s => File.Exists(Path.Combine(_media, s.File))).ToList();
            if (includeTail)
            {
                var last = _file.Segments.LastOrDefault();
                var nextSeq = last is null ? 0 : last.Seq + 1;
                var tailFile = string.Format(Recording.GsrArguments.SegmentPattern.Replace("%06d", "{0:D6}"), nextSeq);
                if (File.Exists(Path.Combine(_media, tailFile)))
                    list.Add(new SegmentEntry { Seq = nextSeq, File = tailFile, Start = last is null ? 0 : last.Start + last.Duration, Duration = 1e9 });
            }
            return list;
        }
    }

    /// <summary>Re-reads the playlist if it changed. Returns true when new segments were added.</summary>
    public bool Refresh()
    {
        var path = PlaylistPath;
        if (!File.Exists(path)) return false;
        var mtime = File.GetLastWriteTimeUtc(path);
        if (mtime == _playlistMtime) return false;
        _playlistMtime = mtime;

        string text;
        try { text = File.ReadAllText(path); } catch (IOException) { return false; }
        var parsed = ParsePlaylist(text);
        var added = false;
        lock (_lock)
        {
            foreach (var (seq, file, duration) in parsed)
            {
                var existing = _file.Segments.FirstOrDefault(s => s.Seq == seq);
                if (existing is not null)
                {
                    if (Math.Abs(existing.Duration - duration) > 0.0005) { existing.Duration = duration; added = true; }
                    continue;
                }
                var prev = _file.Segments.LastOrDefault();
                var start = prev is null ? 0 : prev.Start + prev.Duration;
                if (prev is not null && seq != prev.Seq + 1)
                    start += (seq - prev.Seq - 1) * duration;   // missed segments (daemon lag): assume equal length
                long bytes = 0; try { bytes = new FileInfo(Path.Combine(_media, file)).Length; } catch { }
                _file.Segments.Add(new SegmentEntry { Seq = seq, File = file, Start = start, Duration = duration, Bytes = bytes });
                added = true;
            }
            if (added) Recompute();
            if (added) Persist();
        }
        return added;
    }

    private void Recompute()
    {
        double t = 0; int? lastSeq = null; double lastDur = 0;
        foreach (var s in _file.Segments.OrderBy(s => s.Seq))
        {
            if (lastSeq is int ls && s.Seq != ls + 1) t += (s.Seq - ls - 1) * lastDur;
            s.Start = t; t += s.Duration; lastSeq = s.Seq; lastDur = s.Duration;
        }
    }

    private void Persist()
    {
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_file, SessionJson.Default.SegmentFile));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    /// <summary>Parses "#EXT-X-MEDIA-SEQUENCE" + "#EXTINF" lines into (seq, file, duration).</summary>
    public static List<(int Seq, string File, double Duration)> ParsePlaylist(string text)
    {
        var result = new List<(int, string, double)>();
        int seq = 0; double? pending = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) { int.TryParse(line[22..], out seq); continue; }
            if (line.StartsWith("#EXTINF:"))
            {
                var d = line[8..].TrimEnd(',').Split(',')[0];
                pending = double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (pending is double dur) { result.Add((seq++, Path.GetFileName(line), dur)); pending = null; }
        }
        return result;
    }
}
