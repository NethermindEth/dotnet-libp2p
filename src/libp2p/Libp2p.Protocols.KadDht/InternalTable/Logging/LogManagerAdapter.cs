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
    /// Adapts Microsoft.Extensions.Logging.ILoggerFactory to our ILogManager interface.
    /// </summary>
    public class LogManagerAdapter : ILogManager
    {
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// Creates a new instance of LogManagerAdapter.
        /// </summary>
        /// <param name="loggerFactory">The Microsoft.Extensions.Logging.ILoggerFactory to adapt.</param>
        public LogManagerAdapter(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        /// <inheritdoc />
        public ILogger GetClassLogger<T>()
        {
            return new LoggerAdapter(_loggerFactory.CreateLogger<T>());
        }

        /// <inheritdoc />
        public ILogger GetClassLogger(Type type)
        {
            return new LoggerAdapter(_loggerFactory.CreateLogger(type));
        }

        /// <inheritdoc />
        public ILogger GetLogger(string loggerName)
        {
            return new LoggerAdapter(_loggerFactory.CreateLogger(loggerName));
        }
    }
} 
