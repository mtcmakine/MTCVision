using System.Collections.Concurrent;

namespace MTCVision.Backend;

/// <summary>
/// Writes the log to a file beside the data.
///
/// Running as a Windows service there is no console to watch, and the questions that matter most —
/// did the camera come back after the reboot, did the run restart — are answered by startup
/// messages nobody would otherwise see. Kept deliberately small: one file, rolled by size, no
/// dependency on a logging framework the rest of the app does not use.
/// </summary>
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly string _path = Path.Combine(directory, "mtcvision.log");
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _writeLock = new();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    internal void Write(string line)
    {
        lock (_writeLock)
        {
            try
            {
                // Roll rather than truncate, so the run that led up to a problem is still readable
                // while it is being investigated.
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Copy(_path, _path + ".1", overwrite: true);
                    File.Delete(_path);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // A log that cannot be written must not take the service down with it.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Below Information the framework's own chatter drowns out anything worth reading.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }
    }
}
