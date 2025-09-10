using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KadDhtDemo;

internal sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly ConcurrentDictionary<string, SimpleFileLogger> _loggers = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public SimpleFileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, static (c, state) => new SimpleFileLogger(c, state), _writer);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }

    private sealed class SimpleFileLogger : ILogger
    {
        private readonly string _category;
        private readonly StreamWriter _writer;

        public SimpleFileLogger(string category, StreamWriter writer)
        {
            _category = category;
            _writer = writer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                var message = formatter(state, exception);
                var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
                var line = $"{timestamp} {logLevel,-5} {_category} {message}";
                
                // Truncate very long lines to prevent overflow
                if (line.Length > 2000)
                {
                    line = line.Substring(0, 1997) + "...";
                }
                
                if (exception != null)
                {
                    var exceptionStr = exception.ToString();
                    if (exceptionStr.Length > 500)
                    {
                        exceptionStr = exceptionStr.Substring(0, 497) + "...";
                    }
                    line += " | " + exceptionStr;
                }
                
                _writer.WriteLine(line);
            }
            catch (Exception logEx)
            {
                // Fail silently to prevent logging exceptions from crashing the app
                Console.WriteLine($"Logging error: {logEx.Message}");
            }
        }
    }
}