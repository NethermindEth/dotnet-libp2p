using Microsoft.Extensions.Logging;

namespace PubsubChat;

public class InMemoryLogProvider(List<string> logStore) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new InMemoryLogger(categoryName, logStore);
    }

    public void Dispose()
    {
    }

    private class InMemoryLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly List<string> _logStore;

        public InMemoryLogger(string categoryName, List<string> logStore)
        {
            _categoryName = categoryName;
            _logStore = logStore;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

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

            string message = formatter(state, exception);
            string level = logLevel.ToString().ToLowerInvariant().Substring(0, 4);
            string timestamp = DateTime.Now.ToString("[HH:mm:ss.fff]");

            lock (_logStore)
            {
                _logStore.Add($"{timestamp}{level}: {_categoryName}[{eventId.Id}] {message}");

                // Keep logs limited to avoid excessive memory usage
                if (_logStore.Count > 1000)
                {
                    _logStore.RemoveAt(0);
                }
            }
        }
    }
}
