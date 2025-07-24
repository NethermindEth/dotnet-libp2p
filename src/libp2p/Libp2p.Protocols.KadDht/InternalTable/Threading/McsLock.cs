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


namespace Libp2p.Protocols.KadDht.InternalTable.Threading
{
    /// <summary>
    /// A thread synchronization primitive that uses standard .NET concurrency primitives.
    /// </summary>
    public class McsLock : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly bool _isReentrant;
        private int _ownerThreadId;
        private int _recursionCount;

        /// <summary>
        /// Creates a new instance of McsLock.
        /// </summary>
        /// <param name="isReentrant">If true, the lock can be acquired multiple times by the same thread.</param>
        public McsLock(bool isReentrant = false)
        {
            _semaphore = new SemaphoreSlim(1, 1);
            _isReentrant = isReentrant;
            _ownerThreadId = -1;
            _recursionCount = 0;
        }

        /// <summary>
        /// Acquires the lock synchronously and returns a disposable to release it.
        /// </summary>
        public Disposable Acquire()
        {
            int currentThreadId = Environment.CurrentManagedThreadId;

            if (_isReentrant && _ownerThreadId == currentThreadId)
            {
                _recursionCount++;
                return new Disposable(this);
            }

            _semaphore.Wait();
            _ownerThreadId = currentThreadId;
            _recursionCount = 1;
            return new Disposable(this);
        }

        /// <summary>
        /// Acquires the lock asynchronously.
        /// </summary>
        public async Task AcquireAsync(CancellationToken cancellationToken = default)
        {
            int currentThreadId = Environment.CurrentManagedThreadId;

            if (_isReentrant && _ownerThreadId == currentThreadId)
            {
                _recursionCount++;
                return;
            }

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ownerThreadId = currentThreadId;
            _recursionCount = 1;
        }

        /// <summary>
        /// Releases the lock.
        /// </summary>
        public void Release()
        {
            int currentThreadId = Environment.CurrentManagedThreadId;

            if (_ownerThreadId != currentThreadId)
            {
                throw new InvalidOperationException("Attempt to release a lock that is not owned by the current thread.");
            }

            if (_isReentrant)
            {
                _recursionCount--;
                if (_recursionCount > 0)
                {
                    return;
                }
            }

            _ownerThreadId = -1;
            _recursionCount = 0;
            _semaphore.Release();
        }

        /// <summary>
        /// Disposes the lock.
        /// </summary>
        public void Dispose()
        {
            _semaphore.Dispose();
        }

        /// <summary>
        /// A disposable wrapper for releasing the lock.
        /// </summary>
        public class Disposable : IDisposable
        {
            private readonly McsLock _lock;

            /// <summary>
            /// Creates a new instance of Disposable.
            /// </summary>
            /// <param name="lock">The lock to release when disposed.</param>
            public Disposable(McsLock @lock)
            {
                _lock = @lock;
            }

            /// <summary>
            /// Releases the lock.
            /// </summary>
            public void Dispose()
            {
                _lock.Release();
            }
        }
    }
} 
