// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

public class IdentifyNotifier
{
    public void TrackChanges(LocalPeer libp2pPeer)
    {
        libp2pPeer.ListenAddresses.CollectionChanged += (_, _) =>
        {
            ulong seq = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var session in libp2pPeer.Sessions.ToList())
            {
                _ = session.DialAsync<IdentifyPushProtocol, ulong, ulong>(seq);
            };
        };
    }
}
