// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Libp2p.Protocols.KadDht.Kademlia;

internal class McsLock
{
    internal Disposable Acquire()
    {
        Monitor.Enter(_gate);
        return new Disposable(_gate);
    }

    private readonly object _gate = new object();

    internal class Disposable : IDisposable
    {
        private object? _gate;

        public Disposable(object gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate != null)
            {
                Monitor.Exit(gate);
            }
        }
    }
}
