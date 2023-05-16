// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public class PeerContext : IPeerContext
{
    public IPeer LocalPeer { get; set; }
    public IPeer RemotePeer { get; set; }
    public IEnumerable<IProtocol> ApplayerProtocols { get; init; }
    public MultiAddr RemoteEndpoint { get; set; }
    public MultiAddr LocalEndpoint { get; set; }

    public IPeerContext Fork()
    {
        PeerContext result = (PeerContext)MemberwiseClone();
        result.RemotePeer = ((PeerFactory.RemotePeer)RemotePeer).Fork();
        return result;
    }
}
