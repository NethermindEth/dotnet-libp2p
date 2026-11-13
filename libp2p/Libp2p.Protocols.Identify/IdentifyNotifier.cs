// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

public class IdentifyNotifier
{
    private readonly TimeProvider _timeProvider;
    private long _lastSequence;

    public IdentifyNotifier() : this(TimeProvider.System) { }

    public IdentifyNotifier(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public void TrackChanges(LocalPeer libp2pPeer)
    {
        if (libp2pPeer.GetProtocol<ISessionProtocol<ulong, ulong>>(IdentifyPushProtocol.ProtocolId) is not { } identifyPush)
        {
            return;
        }
        libp2pPeer.ListenAddresses.CollectionChanged += (_, _) =>
        {
            ulong seq = NextSequence();

            foreach (var session in libp2pPeer.Sessions.ToList())
            {
                _ = session.DialAsync(identifyPush, seq);
            }
        };
    }

    private ulong NextSequence()
    {
        long timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        while (true)
        {
            long previous = Interlocked.Read(ref _lastSequence);
            // Wall-clock time can repeat or move backwards between address changes.
            long next = Math.Max(timestamp, checked(previous + 1));
            if (Interlocked.CompareExchange(ref _lastSequence, next, previous) == previous)
            {
                return (ulong)next;
            }
        }
    }
}
