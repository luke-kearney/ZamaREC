using System.Text;

namespace ZamaRec.Daemon;

/// <summary>
/// A small file log for the Windows build, which runs without a console: one line per entry in
/// daemon.log under the data folder, rolled over to daemon.log.1 at 5 MB. Linux logs to the journal instead.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string category) => new FileLogger(this, category);

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                if (_writer is null || _writer.BaseStream.Length > 5 * 1024 * 1024)
                {
                    _writer?.Dispose(); _writer = null;
                    if (File.Exists(_path) && new FileInfo(_path).Length > 5 * 1024 * 1024) File.Move(_path, _path + ".1", overwrite: true);
                    _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                }
                _writer.WriteLine(line);
            }
            catch { }
        }
    }

    public void Dispose() { lock (_lock) { _writer?.Dispose(); _writer = null; } }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information || (level == LogLevel.Debug && category.StartsWith("ZamaRec"));
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var tag = level switch { LogLevel.Trace => "trce", LogLevel.Debug => "dbug", LogLevel.Information => "info", LogLevel.Warning => "warn", LogLevel.Error => "fail", _ => "crit" };
            var name = category.LastIndexOf('.') is var i && i >= 0 ? category[(i + 1)..] : category;
            owner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag} {name}: {fmt(state, ex)}{(ex is null ? "" : "\n" + ex)}");
        }
    }
}
