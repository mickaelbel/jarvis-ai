using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Diagnostics;

/// <summary>Journalise vers %LOCALAPPDATA%\JarvisAI\web.log (diagnostic local).</summary>
public sealed class FileLogProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;

    public FileLogProvider()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI");
        Directory.CreateDirectory(dir);
          var path = Path.Combine(dir, "web.log");
        // FileShare.ReadWrite : plusieurs processus peuvent écrire/lire ce journal
        // (app + tests d'intégration + outil autodev logs) sans IOException.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger : ILogger
    {
        private readonly object _lock = new();
        private readonly StreamWriter _writer;
        private readonly string _category;

        public FileLogger(StreamWriter writer, string category)
        {
            _writer = writer;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {logLevel} {_category}: {formatter(state, exception)}");
                if (exception is not null)
                    _writer.WriteLine(exception.ToString());
            }
        }
    }
}
