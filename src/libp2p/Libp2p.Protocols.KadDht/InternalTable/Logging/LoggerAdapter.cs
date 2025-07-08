// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using Nethermind.Libp2p.Core;


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

        /// <inheritdoc />
        public void Debug(string message)
        {
            _logger.LogDebug(message);
        }

        /// <inheritdoc />
        public void Debug(string message, Exception exception)
        {
            _logger.LogDebug(exception, message);
        }

        /// <inheritdoc />
        public void Info(string message)
        {
            _logger.LogInformation(message);
        }

        /// <inheritdoc />
        public void Info(string message, Exception exception)
        {
            _logger.LogInformation(exception, message);
        }

        /// <inheritdoc />
        public void Warn(string message)
        {
            _logger.LogWarning(message);
        }

        /// <inheritdoc />
        public void Warn(string message, Exception exception)
        {
            _logger.LogWarning(exception, message);
        }

        /// <inheritdoc />
        public void Error(string message)
        {
            _logger.LogError(message);
        }

        /// <inheritdoc />
        public void Error(string message, Exception exception)
        {
            _logger.LogError(exception, message);
        }

        /// <inheritdoc />
        public void Fatal(string message)
        {
            _logger.LogCritical(message);
        }

        /// <inheritdoc />
        public void Fatal(string message, Exception exception)
        {
            _logger.LogCritical(exception, message);
        }

        /// <inheritdoc />
        public void Trace(string message)
        {
            _logger.LogTrace(message);
        }

        /// <inheritdoc />
        public void Trace(string message, Exception exception)
        {
            _logger.LogTrace(exception, message);
        }

        /// <inheritdoc />
        public bool IsDebug => _logger.IsEnabled(LogLevel.Debug);

        /// <inheritdoc />
        public bool IsTrace => _logger.IsEnabled(LogLevel.Trace);

        /// <inheritdoc />
        public bool IsInfo => _logger.IsEnabled(LogLevel.Information);

        /// <inheritdoc />
        public bool IsWarn => _logger.IsEnabled(LogLevel.Warning);

        /// <inheritdoc />
        public bool IsError => _logger.IsEnabled(LogLevel.Error);
    }
} 
