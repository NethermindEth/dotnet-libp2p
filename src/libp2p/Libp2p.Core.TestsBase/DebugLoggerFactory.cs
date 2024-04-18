// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase;

public class DebugLoggerFactory : ILoggerFactory
{
    class DebugLogger(string categoryName) : ILogger, IDisposable
    {
        private readonly string _categoryName = categoryName;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return this;
        }

        public void Dispose()
        {
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            TestContext.Out.WriteLine($"{logLevel} {_categoryName}:{eventId}: {(exception is null ? state?.ToString() : formatter(state, exception))}");
        }
    }

    public void AddProvider(ILoggerProvider provider)
    {

    }

    public ILogger CreateLogger(string categoryName)
    {
        return new DebugLogger(categoryName);
    }

    public void Dispose()
    {

    }
}
