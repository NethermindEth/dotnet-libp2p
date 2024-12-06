// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestContextLoggerFactory : ILoggerFactory
{
    class TestContextLogger(string categoryName) : ILogger, IDisposable
    {
        private readonly string _categoryName = categoryName;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;

        public void Dispose() { }
        public bool IsEnabled(LogLevel logLevel) => true;

        private static string ToString(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRAC",
            LogLevel.Debug => "DEBG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "EROR",
            LogLevel.Critical => "CRIT",
            LogLevel.None => "NONE",
            _ => throw new NotImplementedException()
        };

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string log = $"{ToString(logLevel)} {_categoryName}: {(exception is null ? state?.ToString() : formatter(state, exception))}";
            TestContext.Out.WriteLine(log);
            Debug.WriteLine(log);
        }
    }

    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => new TestContextLogger(categoryName);
    public void Dispose() { }
}
