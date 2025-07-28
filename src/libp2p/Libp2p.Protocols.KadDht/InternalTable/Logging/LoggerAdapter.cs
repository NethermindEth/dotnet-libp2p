// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Extensions.Logging;

namespace Libp2p.Protocols.KadDht.InternalTable.Logging
{
    /// <summary>
    /// Adapts Microsoft.Extensions.Logging.ILogger to our ILogger interface.
    /// </summary>
    public class LoggerAdapter : ILogger
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        /// <summary>
        /// Creates a new instance of LoggerAdapter.
        /// </summary>
        /// <param name="logger">The Microsoft.Extensions.Logging.ILogger to adapt.</param>
        public LoggerAdapter(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Debug(string message)
        {
            _logger.LogDebug(message);
        }

        public void Debug(string message, Exception exception)
        {
            _logger.LogDebug(exception, message);
        }

        public void Info(string message)
        {
            _logger.LogInformation(message);
        }

        public void Info(string message, Exception exception)
        {
            _logger.LogInformation(exception, message);
        }

        public void Warn(string message)
        {
            _logger.LogWarning(message);
        }

        public void Warn(string message, Exception exception)
        {
            _logger.LogWarning(exception, message);
        }

        public void Error(string message)
        {
            _logger.LogError(message);
        }

        public void Error(string message, Exception exception)
        {
            _logger.LogError(exception, message);
        }

        public void Fatal(string message)
        {
            _logger.LogCritical(message);
        }

        public void Fatal(string message, Exception exception)
        {
            _logger.LogCritical(exception, message);
        }

        public void Trace(string message)
        {
            _logger.LogTrace(message);
        }

        public void Trace(string message, Exception exception)
        {
            _logger.LogTrace(exception, message);
        }

        public bool IsDebug => _logger.IsEnabled(LogLevel.Debug);
        public bool IsTrace => _logger.IsEnabled(LogLevel.Trace);
        public bool IsInfo => _logger.IsEnabled(LogLevel.Information);
        public bool IsWarn => _logger.IsEnabled(LogLevel.Warning);
        public bool IsError => _logger.IsEnabled(LogLevel.Error);
        public void LogDebug<TNode>(Exception exception, string errorWhileRefreshingNodeNode, TNode toRefresh) where TNode : notnull
        {
            throw new NotImplementedException();
        }
    }
}
