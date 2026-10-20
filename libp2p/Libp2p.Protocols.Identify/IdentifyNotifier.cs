// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

public class IdentifyNotifier
{
    public void TrackChanges(LocalPeer libp2pPeer)
    {
        if (libp2pPeer.GetProtocol(IdentifyPushProtocol.ProtocolId) is not ISessionProtocol<ulong, ulong> identifyPush)
        {
            return;
        }
        libp2pPeer.ListenAddresses.CollectionChanged += (_, _) =>
        {
            ulong seq = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var session in libp2pPeer.Sessions.ToList())
            {
                _ = session.DialAsync(identifyPush, seq);
            }
        };
    }
}
