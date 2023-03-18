// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public class PeerContext : IPeerContext
{
    public IPeer LocalPeer { get; set; }
    public IPeer RemotePeer { get; set; }
    public IProtocol[] ApplayerProtocols { get; }
    public MultiAddr RemoteEndpoint { get; set; }
    public MultiAddr LocalEndpoint { get; set; }

    public IPeerContext Fork()
    {
        return (PeerContext)MemberwiseClone();
    }
}