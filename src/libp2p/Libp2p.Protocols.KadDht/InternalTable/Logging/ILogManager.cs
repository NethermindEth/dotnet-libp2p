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

namespace Libp2p.Protocols.KadDht.InternalTable.Logging
{
    /// <summary>
    /// Interface for managing loggers.
    /// </summary>
    public interface ILogManager
    {
        /// <summary>
        /// Gets a logger for the specified type.
        /// </summary>
        /// <typeparam name="T">The type to get a logger for.</typeparam>
        /// <returns>A logger for the specified type.</returns>
        ILogger GetClassLogger<T>();

        /// <summary>
        /// Gets a logger for the specified type.
        /// </summary>
        /// <param name="type">The type to get a logger for.</param>
        /// <returns>A logger for the specified type.</returns>
        ILogger GetClassLogger(Type type);

        /// <summary>
        /// Gets a logger with the specified name.
        /// </summary>
        /// <param name="loggerName">The name of the logger.</param>
        /// <returns>A logger with the specified name.</returns>
        ILogger GetLogger(string loggerName);
    }
} 
